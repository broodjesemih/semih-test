using Godot;
using System.Collections.Generic;
using System.Globalization;

[Tool]
public partial class CsvStairGenerator : MeshInstance3D
{
	// ---------- Bestands- & algemene opties ----------
	[Export] public string CsvPath { get; set; } = "res://data/StairPoints.csv";
	[Export] public bool BuildOnReady { get; set; } = true;
	[Export] public bool DrawDebugVectors { get; set; } = false;
	[Export] public float DebugVectorScale { get; set; } = 0.25f;
	[Export] public bool RebuildNow { get; set; } = false;

	// ---------- TREDE-MODUS ----------
	[Export] public bool UseSteps { get; set; } = true;
	[Export] public float Width { get; set; } = 1.0f;        // constante tredebreedte
	[Export] public float TreadDepth { get; set; } = 0.28f;  // “lengte” van trede
	[Export] public float TreadThickness { get; set; } = 0.04f;

	// Hoogte-groepering uit CSV
	[Export] public float LevelEpsilon { get; set; } = 0.002f; // m

	// ---------- LANDING-DETECTIE ----------
	[Export] public bool MakePlatformsAtTurns { get; set; } = true;
	[Export] public float TurnAngleDeg { get; set; } = 35f;      // bocht-drempel
	[Export] public float PlatformHeightEps { get; set; } = 0.005f; // vlak-hoogte tolerantie

	// ---------- RAMP-MODUS ----------
	[Export] public bool UseRampMode { get; set; } = false;
	[Export] public float Thickness { get; set; } = 0.12f;
	[Export] public bool ClosedPath { get; set; } = false;
	[Export] public float UvRepeatPerMeter { get; set; } = 1f;

	private readonly CultureInfo _inv = CultureInfo.InvariantCulture;

	public override void _Ready()
	{
		if (BuildOnReady) Rebuild();
	}

	public override void _Process(double delta)
	{
		if (Engine.IsEditorHint() && RebuildNow)
		{
			RebuildNow = false;
			Rebuild();
		}
	}

	public void Rebuild()
	{
		if (!FileAccess.FileExists(CsvPath))
		{
			GD.PushError($"CSV niet gevonden: {CsvPath}");
			return;
		}

		var spine = LoadPointsFromCsv(CsvPath);
		if (spine.Count < 2)
		{
			GD.PushError("Niet genoeg punten in CSV.");
			return;
		}

		// ====== TREDE/PLATFORM-MODUS ======
		if (UseSteps && !UseRampMode)
		{
			// Extents per hoogte (voor platforms) + centers (voor richting)
			var levels  = GroupByHeight(spine, LevelEpsilon); // gesorteerd op Y
			var centers = new List<Vector3>(levels.Count);
			foreach (var lv in levels)
				centers.Add(new Vector3((lv.minX + lv.maxX) * 0.5f, lv.y, (lv.minZ + lv.maxZ) * 0.5f));

			var tangents = BuildTangents(centers, false);
			var rights   = BuildRights(tangents);

			var st = new SurfaceTool();
			st.Begin(Mesh.PrimitiveType.Triangles);

			for (int i = 0; i < centers.Count; i++)
			{
				// LANDING?
				if (MakePlatformsAtTurns && IsLandingIndex(i, centers, tangents))
				{
					// verzamel alle buckets op ~dezelfde hoogte
					float y0 = centers[i].Y;
					int start = i, end = i;
					while (start > 0 && Mathf.Abs(centers[start - 1].Y - y0) <= PlatformHeightEps) start--;
					while (end < centers.Count - 1 && Mathf.Abs(centers[end + 1].Y - y0) <= PlatformHeightEps) end++;

					// union extents
					float minX = float.MaxValue, maxX = float.MinValue;
					float minZ = float.MaxValue, maxZ = float.MinValue;
					for (int k = start; k <= end; k++)
					{
						if (levels[k].minX < minX) minX = levels[k].minX;
						if (levels[k].maxX > maxX) maxX = levels[k].maxX;
						if (levels[k].minZ < minZ) minZ = levels[k].minZ;
						if (levels[k].maxZ > maxZ) maxZ = levels[k].maxZ;
					}

					// één platform-plaat
					AddStepBox(st, y0, minX, maxX, minZ, maxZ, TreadThickness);

					// sla de verwerkte indices over
					i = end;
					continue;
				}

				// NORMALE TREDE (constante breedte)
				var c = centers[i];
				Vector3? prev = i > 0 ? centers[i - 1] : (Vector3?)null;
				Vector3? next = i < centers.Count - 1 ? centers[i + 1] : (Vector3?)null;

				var right = rights[i];
				var leftPt  = c - right * (Width * 0.5f);
				var rightPt = c + right * (Width * 0.5f);

				AddTread(st, leftPt, rightPt, prev, next, TreadDepth, TreadThickness);
			}

			st.GenerateNormals();
			Mesh = st.Commit();
			BuildOrRefreshCollider(Mesh);

			if (DrawDebugVectors) BuildDebugLines(centers, tangents, rights);
			return;
		}

		// ====== RAMP-MODUS ======
		{
			var tangents = BuildTangents(spine, ClosedPath);
			var rights   = BuildRights(tangents);

			var left  = new List<Vector3>(spine.Count);
			var right = new List<Vector3>(spine.Count);
			for (int i = 0; i < spine.Count; i++)
			{
				left.Add(  spine[i] - rights[i] * (Width * 0.5f) );
				right.Add( spine[i] + rights[i] * (Width * 0.5f) );
			}

			var topL = left;
			var topR = right;
			var botL = OffsetDown(left,  Thickness);
			var botR = OffsetDown(right, Thickness);

			var dists = CumulativeDistances(spine);

			var stRamp = new SurfaceTool();
			stRamp.Begin(Mesh.PrimitiveType.Triangles);

			int segCount = ClosedPath ? spine.Count : spine.Count - 1;

			// Bovenkant
			for (int i = 0; i < segCount; i++)
			{
				int i0 = i;
				int i1 = (i + 1) % spine.Count;

				AddQuad(stRamp,
					topL[i0], topR[i0], topR[i1], topL[i1],
					new Vector2(dists[i0] * UvRepeatPerMeter, 0),
					new Vector2(dists[i0] * UvRepeatPerMeter, 1),
					new Vector2(dists[i1] * UvRepeatPerMeter, 1),
					new Vector2(dists[i1] * UvRepeatPerMeter, 0));
			}

			// Onderkant
			for (int i = 0; i < segCount; i++)
			{
				int i0 = i;
				int i1 = (i + 1) % spine.Count;

				AddQuad(stRamp,
					botL[i1], botR[i1], botR[i0], botL[i0],
					new Vector2(dists[i1] * UvRepeatPerMeter, 0),
					new Vector2(dists[i1] * UvRepeatPerMeter, 1),
					new Vector2(dists[i0] * UvRepeatPerMeter, 1),
					new Vector2(dists[i0] * UvRepeatPerMeter, 0));
			}

			// Zijkanten
			for (int i = 0; i < segCount; i++)
			{
				int i0 = i;
				int i1 = (i + 1) % spine.Count;

				AddQuad(stRamp,
					topL[i0], topL[i1], botL[i1], botL[i0],
					new Vector2(0, dists[i0] * UvRepeatPerMeter),
					new Vector2(0, dists[i1] * UvRepeatPerMeter),
					new Vector2(1, dists[i1] * UvRepeatPerMeter),
					new Vector2(1, dists[i0] * UvRepeatPerMeter));

				AddQuad(stRamp,
					topR[i1], topR[i0], botR[i0], botR[i1],
					new Vector2(0, dists[i1] * UvRepeatPerMeter),
					new Vector2(0, dists[i0] * UvRepeatPerMeter),
					new Vector2(1, dists[i0] * UvRepeatPerMeter),
					new Vector2(1, dists[i1] * UvRepeatPerMeter));
			}

			// Eindkappen
			if (!ClosedPath)
			{
				AddQuad(stRamp, topL[0], topR[0], botR[0], botL[0],
					new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1));

				int last = spine.Count - 1;
				AddQuad(stRamp, topL[last], botL[last], botR[last], topR[last],
					new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0));
			}

			Mesh = stRamp.Commit();
			BuildOrRefreshCollider(Mesh);

			if (DrawDebugVectors) BuildDebugLines(spine, tangents, rights);
		}
	}

	// ------------------- Helpers -------------------

	private void BuildOrRefreshCollider(Mesh mesh)
	{
		var body = GetNodeOrNull<StaticBody3D>("Collider") ?? new StaticBody3D { Name = "Collider" };
		if (body.GetParent() == null) AddChild(body);
		foreach (Node c in body.GetChildren()) c.QueueFree();
		if (mesh == null) return;
		var shape = mesh.CreateTrimeshShape();
		var cs = new CollisionShape3D { Shape = shape };
		body.AddChild(cs);
	}

	private List<Vector3> LoadPointsFromCsv(string path)
	{
		var pts = new List<Vector3>();

		using var f = FileAccess.Open(path, FileAccess.ModeFlags.Read);
		bool headerSkipped = false;

		while (!f.EofReached())
		{
			var raw = f.GetLine();
			if (raw == null) break;

			var line = raw.Trim();
			if (string.IsNullOrEmpty(line)) continue;

			char delim = line.Contains(';') ? ';' : ',';

			if (!headerSkipped)
			{
				var first = line.Split(delim)[0].Trim().Trim('"');
				if (first.Contains('X') || first.Contains('x'))
				{
					headerSkipped = true;
					continue;
				}
			}

			var parts = line.Split(delim);
			if (parts.Length < 3) continue;

			string sx = parts[0].Trim().Trim('"');
			string sy = parts[1].Trim().Trim('"');
			string sz = parts[2].Trim().Trim('"');

			if (!float.TryParse(sx, NumberStyles.Float, _inv, out var x)) continue;
			if (!float.TryParse(sy, NumberStyles.Float, _inv, out var y)) continue;
			if (!float.TryParse(sz, NumberStyles.Float, _inv, out var z)) continue;

			// CSV: Z (3e kolom) is hoogte → Godot Y
			pts.Add(new Vector3(x, z, y));
		}

		if (pts.Count >= 2 && pts[0].DistanceTo(pts[^1]) < 1e-4f)
		{
			pts.RemoveAt(pts.Count - 1);
			ClosedPath = true;
		}

		GD.Print($"CSV geladen: {pts.Count} punten uit {path} (ClosedPath={ClosedPath})");
		return pts;
	}

	// Extents per hoogte
	private struct StepLevel { public float y, minX, maxX, minZ, maxZ; }

	private List<StepLevel> GroupByHeight(List<Vector3> pts, float eps)
	{
		var buckets = new Dictionary<int, List<Vector3>>();
		foreach (var p in pts)
		{
			int key = Mathf.RoundToInt(p.Y / eps);
			if (!buckets.TryGetValue(key, out var list))
			{
				list = new List<Vector3>();
				buckets[key] = list;
			}
			list.Add(p);
		}

		var keys = new List<int>(buckets.Keys);
		keys.Sort();

		var levels = new List<StepLevel>(keys.Count);
		foreach (var key in keys)
		{
			var list = buckets[key];
			float ySum = 0f;
			float minX = float.MaxValue, maxX = float.MinValue;
			float minZ = float.MaxValue, maxZ = float.MinValue;

			foreach (var p in list)
			{
				ySum += p.Y;
				if (p.X < minX) minX = p.X;
				if (p.X > maxX) maxX = p.X;
				if (p.Z < minZ) minZ = p.Z;
				if (p.Z > maxZ) maxZ = p.Z;
			}

			float yAvg = ySum / list.Count;
			levels.Add(new StepLevel { y = yAvg, minX = minX, maxX = maxX, minZ = minZ, maxZ = maxZ });
		}

		return levels;
	}

// Rechthoekige plaat/box op hoogte y met dikte th
private void AddStepBox(
	SurfaceTool st,
	float y,
	float minX, float maxX,
	float minZ, float maxZ,
	float th)
{
	Vector3 A = new Vector3(minX, y, minZ);
	Vector3 B = new Vector3(maxX, y, minZ);
	Vector3 C = new Vector3(maxX, y, maxZ);
	Vector3 D = new Vector3(minX, y, maxZ);

	Vector3 A2 = A + Vector3.Down * th;
	Vector3 B2 = B + Vector3.Down * th;
	Vector3 C2 = C + Vector3.Down * th;
	Vector3 D2 = D + Vector3.Down * th;

	// Boven
	AddQuad(st, A, B, C, D,
		new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1));

	// Onder (winding omdraaien)
	AddQuad(st, D2, C2, B2, A2,
		new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1));

	// Zijkanten
	AddQuad(st, A, D, D2, A2, new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1)); // links
	AddQuad(st, B, A, A2, B2, new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1)); // voor
	AddQuad(st, C, B, B2, C2, new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1)); // rechts
	AddQuad(st, D, C, C2, D2, new Vector2(0,0), new Vector2(1,0), new Vector2(1,1), new Vector2(0,1)); // achter
}

private bool IsLandingIndex(int i, List<Vector3> centers, List<Vector3> tangents)
{
	if (i <= 0 || i >= centers.Count - 1) return false;

	bool flat = Mathf.Abs(centers[i].Y - centers[i - 1].Y) <= 1e-4f &&
				Mathf.Abs(centers[i + 1].Y - centers[i].Y) <= 1e-4f;
	if (!flat) return false;

	var a = tangents[i - 1].Normalized();
	var b = tangents[i + 1].Normalized();
	float dot = Mathf.Clamp(a.Dot(b), -1f, 1f);
	float ang = Mathf.RadToDeg(Mathf.Acos(dot));
	return ang >= TurnAngleDeg; // gebruikt de exportwaarde
}


	private static List<Vector3> BuildTangents(List<Vector3> p, bool closed)
	{
		var t = new List<Vector3>(p.Count);
		for (int i = 0; i < p.Count; i++)
		{
			int iPrev = i - 1; if (iPrev < 0) iPrev = closed ? p.Count - 1 : 0;
			int iNext = i + 1; if (iNext >= p.Count) iNext = closed ? 0 : p.Count - 1;
			var dir = (p[iNext] - p[iPrev]);
			dir.Y = 0; // horizontaal
			dir = dir.Length() < 1e-5f ? Vector3.Forward : dir.Normalized();
			t.Add(dir);
		}
		return t;
	}

	private static List<Vector3> BuildRights(List<Vector3> tangents)
	{
		var rights = new List<Vector3>(tangents.Count);
		for (int i = 0; i < tangents.Count; i++)
		{
			var r = tangents[i].Cross(Vector3.Up).Normalized();
			if (r.Length() < 1e-5f) r = Vector3.Right;
			rights.Add(r);
		}
		return rights;
	}

	private static List<Vector3> OffsetDown(List<Vector3> pts, float d)
	{
		var res = new List<Vector3>(pts.Count);
		for (int i = 0; i < pts.Count; i++) res.Add(pts[i] + Vector3.Down * d);
		return res;
	}

	private static float[] CumulativeDistances(List<Vector3> pts)
	{
		var d = new float[pts.Count];
		if (pts.Count == 0) return d;
		d[0] = 0f;
		for (int i = 1; i < pts.Count; i++)
			d[i] = d[i - 1] + pts[i].DistanceTo(pts[i - 1]);
		return d;
	}

	private static void AddQuad(SurfaceTool st,
		Vector3 a, Vector3 b, Vector3 c, Vector3 d,
		Vector2 uva, Vector2 uvb, Vector2 uvc, Vector2 uvd)
	{
		var n = (b - a).Cross(c - a).Normalized();

		st.SetNormal(n); st.SetUV(uva); st.AddVertex(a);
		st.SetNormal(n); st.SetUV(uvb); st.AddVertex(b);
		st.SetNormal(n); st.SetUV(uvc); st.AddVertex(c);

		st.SetNormal(n); st.SetUV(uva); st.AddVertex(a);
		st.SetNormal(n); st.SetUV(uvc); st.AddVertex(c);
		st.SetNormal(n); st.SetUV(uvd); st.AddVertex(d);
	}

	private void BuildDebugLines(List<Vector3> pts, List<Vector3> tangents, List<Vector3> rights)
	{
		var st = new SurfaceTool();
		st.Begin(Mesh.PrimitiveType.Lines);

		for (int i = 0; i < pts.Count; i++)
		{
			var p = pts[i];

			st.SetColor(new Color(1, 0, 0)); st.AddVertex(p); st.AddVertex(p + tangents[i] * DebugVectorScale);
			st.SetColor(new Color(0, 1, 0)); st.AddVertex(p); st.AddVertex(p + rights[i] * DebugVectorScale);
			st.SetColor(new Color(0, 0, 1)); st.AddVertex(p); st.AddVertex(p + Vector3.Up * DebugVectorScale);
		}

		var lineMesh = st.Commit();
		var lines = GetNodeOrNull<MeshInstance3D>("DebugVectors") ?? new MeshInstance3D { Name = "DebugVectors" };
		if (lines.GetParent() == null) AddChild(lines);
		lines.Mesh = lineMesh;
	}

	// Trede-doos met vaste breedte/diepte
	private void AddTread(
		SurfaceTool st,
		Vector3 left, Vector3 right,
		Vector3? prevMid, Vector3? nextMid,
		float depth, float thickness)
	{
		// breedte-as
		var width = right - left;
		width.Y = 0;
		var wlen = width.Length();
		if (wlen < 1e-5f) return;
		width /= wlen;

		// voorwaarts (uit middenlijn, horizontaal)
		Vector3 forward = Vector3.Zero;
		var currMid = (left + right) * 0.5f;

		if (prevMid.HasValue && nextMid.HasValue)
			forward = (nextMid.Value - prevMid.Value);
		else if (nextMid.HasValue)
			forward = (nextMid.Value - currMid);
		else if (prevMid.HasValue)
			forward = (currMid - prevMid.Value);

		forward.Y = 0;
		if (forward.Length() < 1e-5f) forward = Vector3.Up.Cross(width);
		forward = forward.Normalized();

		float halfD = depth * 0.5f;
		var fl = left  + forward * halfD;
		var fr = right + forward * halfD;
		var bl = left  - forward * halfD;
		var br = right - forward * halfD;

		// boven
		var nTop = (fr - fl).Cross(br - fl).Normalized();
		st.SetNormal(nTop); st.AddVertex(fl);
		st.SetNormal(nTop); st.AddVertex(fr);
		st.SetNormal(nTop); st.AddVertex(br);

		st.SetNormal(nTop); st.AddVertex(fl);
		st.SetNormal(nTop); st.AddVertex(br);
		st.SetNormal(nTop); st.AddVertex(bl);

		// naar beneden
		var down = Vector3.Down * thickness;
		var flb = fl + down; var frb = fr + down;
		var blb = bl + down; var brb = br + down;

		AddTriQuad(st, bl, blb, flb, fl); // links
		AddTriQuad(st, fr, frb, brb, br); // rechts
		AddTriQuad(st, fl, flb, frb, fr); // voor
		AddTriQuad(st, br, brb, blb, bl); // achter

		// onder
		var nBottom = (brb - frb).Cross(flb - frb).Normalized();
		st.SetNormal(nBottom); st.AddVertex(flb);
		st.SetNormal(nBottom); st.AddVertex(frb);
		st.SetNormal(nBottom); st.AddVertex(brb);

		st.SetNormal(nBottom); st.AddVertex(flb);
		st.SetNormal(nBottom); st.AddVertex(brb);
		st.SetNormal(nBottom); st.AddVertex(blb);
	}

	private static void AddTriQuad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
	{
		var n = (b - a).Cross(c - a).Normalized();
		st.SetNormal(n); st.AddVertex(a);
		st.SetNormal(n); st.AddVertex(b);
		st.SetNormal(n); st.AddVertex(c);
		st.SetNormal(n); st.AddVertex(a);
		st.SetNormal(n); st.AddVertex(c);
		st.SetNormal(n); st.AddVertex(d);
	}
}
