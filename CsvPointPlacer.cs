using Godot;
using System.Collections.Generic;
using System.Globalization;

[Tool]
public partial class CsvPointPlacer : MultiMeshInstance3D
{
	[Export] public bool ConnectRowsByY = true;        // rij: zelfde Y, sorteer op X
	[Export] public bool ConnectColumnsByX = true;     // kolom X: zelfde X, sorteer op Y
	[Export] public bool ConnectColumnsByZ = true;     // kolom Z: zelfde Z, sorteer op Y


	[Export] public float ColumnXEpsilon = 0.01f;      // X-bucket tolerantie
	[Export] public float ColumnZEpsilon = 0.01f;      // Z-bucket tolerantie

	// max toelaatbare afstand voor een verbinding (0 = geen limiet)
	[Export] public float MaxRowGap = 4.0f;            // in meters, rijen (Y) - max afstand tussen punten op trede
	[Export] public float MaxColXGap = 0.0f;           // kolommen (X)
	[Export] public float MaxColZGap = 0.0f;           // kolommen (Z)
	
	[Export] public bool UseXOnlyForLength { get; set; } = true; // witte pijl: alleen X
	[Export] public bool ChooseClosestPair { get; set; } = true; // bij >2 punten: dichtstbij

	[Export] public float LineThickness { get; set; } = 0.005f; // meter
	[Export] public string CsvPath { get; set; } = "res://data/StairPoints.csv";
	[Export] public bool BuildOnReady { get; set; } = true;
	[Export] public bool BuildInEditor { get; set; } = false;  // Nieuw: toon trap in editor
	[Export] public bool RebuildNow { get; set; } = false;

	// punten
	[Export] public float PointSize { get; set; } = 0.01f;
	[Export] public bool UseSpheres { get; set; } = true;
	[Export] public Color PointColor { get; set; } = new Color(1, 0, 0);

	// Corner points (tussenpunten bij rechte hoeken)
	[Export] public bool DrawCornerPoints { get; set; } = true;
	[Export] public float CornerPointSize { get; set; } = 0.01f;
	[Export] public Color CornerPointColor { get; set; } = new Color(1, 1, 1); // Wit

	// lijnen per hoogtebucket (stuk van trede)
	[Export] public bool DrawLevelLines { get; set; } = true;
	[Export] public float LevelEpsilon { get; set; } = 0.25f;      // punten horen samen als |ΔY| <= epsilon (20cm voor trapmetingen met meetfouten)
	[Export] public float LineRadius { get; set; } = 0.01f;        // "dikte" van de lijn (cilinder)
	[Export] public Color LineColor { get; set; } = new Color(0, 1, 0);
	
	// Debug Mode - toggle groene vectoren
	private bool _debugMode = false;
	
	// Gevulde treden
	[Export] public bool DrawStairSteps { get; set; } = true;
	[Export] public Color StepColor { get; set; } = new Color(0.8f, 0.8f, 0.8f, 0.7f); // Lichtgrijs, semi-transparant

	// Auto-correct parameters
	[Export] public float MergeDistanceThreshold { get; set; } = 0.05f;    // 5cm - punten dichterbij worden samengevoegd
	[Export] public float ClusterMergeRadius { get; set; } = 0.15f;        // 15cm - radius voor cluster detectie
	[Export] public float HeightSnapThreshold { get; set; } = 0.09f;       // 3cm - hoogte snappen naar niveau

	private readonly CultureInfo _inv = CultureInfo.InvariantCulture;
	private enum SegmentMode { RowsByY_SortX, ColsByX_SortY, ColsByZ_SortY }
	
	// Edit mode data
	private List<Vector3> _currentPoints = new List<Vector3>();
	private List<Vector3> _currentCornerPoints = new List<Vector3>();
	private EditModeManager _editModeManager;
	
	// Undo/Redo system
	private List<(List<Vector3> points, List<Vector3> corners)> _undoStack = new List<(List<Vector3>, List<Vector3>)>();
	private List<(List<Vector3> points, List<Vector3> corners)> _redoStack = new List<(List<Vector3>, List<Vector3>)>();
	private const int MaxUndoSteps = 50; // Maximaal aantal undo's

	public override void _Ready()
	{
		// In de editor: alleen builden als BuildInEditor aan staat
		if (Engine.IsEditorHint())
		{
			if (BuildInEditor && BuildOnReady)
			{
				Rebuild();
			}
		}
		// In game: builden als BuildOnReady aan staat
		else 
		{
			if (BuildOnReady)
			{
				Rebuild();
			}
			
			// EditModeManager ALTIJD toevoegen tijdens runtime (niet in editor)
			_editModeManager = new EditModeManager();
			AddChild(_editModeManager);
			GD.Print("[CsvPointPlacer] EditModeManager aangemaakt");
		}
	}
	
	// Public methods voor edit mode
	public List<Vector3> GetPoints() => new List<Vector3>(_currentPoints);
	public List<Vector3> GetCornerPoints() => new List<Vector3>(_currentCornerPoints);
	
	public void UpdatePoint(int index, Vector3 newPosition)
	{
		if (index >= 0 && index < _currentPoints.Count)
		{
			_currentPoints[index] = newPosition;
		}
	}
	
	public void UpdateCornerPoint(int index, Vector3 newPosition)
	{
		if (index >= 0 && index < _currentCornerPoints.Count)
		{
			_currentCornerPoints[index] = newPosition;
		}
	}
	
	// Nieuwe methode: opslaan van undo state vanuit edit mode
	public void SaveEditUndoState()
	{
		SaveUndoState();
	}
	
	public void RebuildFromCurrentData()
	{
		// Verwijder ALLE oude visualisaties voordat we rebuilden
		ClearAllVisualizations();
		
		BuildPoints(_currentPoints);
		BuildLevelLinesFromData(_currentPoints, _currentCornerPoints);
		BuildCornerPoints(_currentCornerPoints);
		BuildStairSteps(_currentPoints, _currentCornerPoints);
	}
	
	private void ClearAllVisualizations()
	{
		// Verwijder oude level lines/bars - DIRECT met Free() niet QueueFree()
		var oldBars = GetNodeOrNull<MultiMeshInstance3D>("LevelBars");
		if (oldBars != null)
		{
			RemoveChild(oldBars);
			oldBars.Free();
		}
		
		// Verwijder oude corner points
		var oldCorners = GetNodeOrNull<MultiMeshInstance3D>("CornerPoints");
		if (oldCorners != null)
		{
			RemoveChild(oldCorners);
			oldCorners.Free();
		}
		
		// Verwijder oude stair steps
		var oldSteps = GetNodeOrNull<Node3D>("StairSteps");
		if (oldSteps != null)
		{
			RemoveChild(oldSteps);
			oldSteps.Free();
		}
		
		// Reset de main multimesh (de rode punten)
		Multimesh = null;
	}
	
	public void ToggleEditMode()
	{
		if (_editModeManager != null)
		{
			_editModeManager.ToggleEditMode();
		}
		else
		{
			GD.PrintErr("[CsvPointPlacer] EditModeManager is NULL! Kan edit mode niet togglen.");
		}
	}
	
	public bool IsEditMode()
	{
		return _editModeManager != null && _editModeManager.IsEditMode;
	}
	
	public void ToggleDebugMode()
	{
		_debugMode = !_debugMode;
		GD.Print($"[DEBUG MODE] {(_debugMode ? "AAN" : "UIT")}");
		
		// Herbouw visualisatie om groene lijnen te tonen/verbergen
		RebuildFromCurrentData();
	}
	
	public bool IsDebugMode()
	{
		return _debugMode;
	}
	
	// Undo/Redo systeem
	private void SaveUndoState()
	{
		// Sla huidige staat op in undo stack (zowel points als corners)
		var pointsCopy = new List<Vector3>(_currentPoints);
		var cornersCopy = new List<Vector3>(_currentCornerPoints);
		_undoStack.Add((pointsCopy, cornersCopy));
		
		// Limiteer stack grootte
		if (_undoStack.Count > MaxUndoSteps)
		{
			_undoStack.RemoveAt(0);
		}
		
		// Clear redo stack bij nieuwe actie
		_redoStack.Clear();
		
		GD.Print($"[UNDO] State opgeslagen ({_undoStack.Count} states in stack)");
	}
	
	public void Undo()
	{
		if (_undoStack.Count == 0)
		{
			GD.Print("[UNDO] Niets om te undo'en");
			return;
		}
		
		// Sla huidige staat op in redo stack
		var currentPointsCopy = new List<Vector3>(_currentPoints);
		var currentCornersCopy = new List<Vector3>(_currentCornerPoints);
		_redoStack.Add((currentPointsCopy, currentCornersCopy));
		
		// Haal vorige staat terug
		var previousState = _undoStack[_undoStack.Count - 1];
		_undoStack.RemoveAt(_undoStack.Count - 1);
		
		_currentPoints = new List<Vector3>(previousState.points);
		_currentCornerPoints = new List<Vector3>(previousState.corners);
		
		GD.Print($"[UNDO] State hersteld ({_undoStack.Count} undo's over)");
		
		// Rebuild visualisatie
		RebuildFromCurrentData();
		
		// Refresh edit points als we in edit mode zijn
		if (_editModeManager != null && IsEditMode())
		{
			_editModeManager.RefreshEditPoints();
		}
	}
	
	public void Redo()
	{
		if (_redoStack.Count == 0)
		{
			GD.Print("[REDO] Niets om te redo'en");
			return;
		}
		
		// Sla huidige staat op in undo stack
		var currentPointsCopy = new List<Vector3>(_currentPoints);
		var currentCornersCopy = new List<Vector3>(_currentCornerPoints);
		_undoStack.Add((currentPointsCopy, currentCornersCopy));
		
		// Haal redo staat terug
		var redoState = _redoStack[_redoStack.Count - 1];
		_redoStack.RemoveAt(_redoStack.Count - 1);
		
		_currentPoints = new List<Vector3>(redoState.points);
		_currentCornerPoints = new List<Vector3>(redoState.corners);
		
		GD.Print($"[REDO] State hersteld ({_redoStack.Count} redo's over)");
		
		// Rebuild visualisatie
		RebuildFromCurrentData();
		
		// Refresh edit points als we in edit mode zijn
		if (_editModeManager != null && IsEditMode())
		{
			_editModeManager.RefreshEditPoints();
		}
	}
	
	public bool CanUndo() => _undoStack.Count > 0;
	public bool CanRedo() => _redoStack.Count > 0;
	
	public void AutoCorrectCSV()
	{
		if (_currentPoints == null || _currentPoints.Count == 0)
		{
			GD.PrintErr("[AUTO CORRECT] Geen punten om te corrigeren!");
			return;
		}
		
		// Save state before auto-correct
		SaveUndoState();
		
		GD.Print($"[AUTO CORRECT] Start met {_currentPoints.Count} punten");
		
		var correctedPoints = new List<Vector3>(_currentPoints);
		
		// STAP 1A: Merge opeenvolgende punten die te dicht bij elkaar zitten (meetfouten)
		// Dit is belangrijker omdat CSV punten in volgorde zijn gemeten
		int sequentialMerged = 0;
		for (int i = correctedPoints.Count - 1; i >= 1; i--)
		{
			float dist = (correctedPoints[i] - correctedPoints[i - 1]).Length();
			
			if (dist < MergeDistanceThreshold)
			{
				// Vervang beide punten door gemiddelde positie
				Vector3 avgPos = (correctedPoints[i] + correctedPoints[i - 1]) * 0.5f;
				correctedPoints[i - 1] = avgPos;
				correctedPoints.RemoveAt(i);
				sequentialMerged++;
			}
		}
		
		GD.Print($"[AUTO CORRECT] Stap 1A: {sequentialMerged} opeenvolgende punten samengevoegd");
		
		// STAP 1B: Merge alle overige punten die te dicht bij elkaar zitten
		int globalMerged = 0;
		for (int i = correctedPoints.Count - 1; i >= 1; i--)
		{
			for (int j = i - 1; j >= 0; j--)
			{
				float dist = (correctedPoints[i] - correctedPoints[j]).Length();
				
				if (dist < MergeDistanceThreshold)
				{
					// Vervang beide punten door gemiddelde positie
					Vector3 avgPos = (correctedPoints[i] + correctedPoints[j]) * 0.5f;
					correctedPoints[j] = avgPos;
					correctedPoints.RemoveAt(i);
					globalMerged++;
					break;
				}
			}
		}
		
		GD.Print($"[AUTO CORRECT] Stap 1B: {globalMerged} globale duplicaten samengevoegd");
		int totalMerged = sequentialMerged + globalMerged;
		GD.Print($"[AUTO CORRECT] Stap 1 totaal: {totalMerged} punten samengevoegd (was {_currentPoints.Count}, nu {correctedPoints.Count})");
		
		// STAP 1C: Cluster-based smoothing - vind clusters van punten en vervang door centrum
		int clustersFound = 0;
		int clustersSmoothed = 0;
		bool changed = true;
		
		while (changed && correctedPoints.Count > 0)
		{
			changed = false;
			var processed = new HashSet<int>();
			
			for (int i = 0; i < correctedPoints.Count; i++)
			{
				if (processed.Contains(i)) continue;
				
				// Vind alle punten binnen ClusterMergeRadius
				var cluster = new List<int> { i };
				processed.Add(i);
				
				for (int j = 0; j < correctedPoints.Count; j++)
				{
					if (i == j || processed.Contains(j)) continue;
					
					float dist = (correctedPoints[j] - correctedPoints[i]).Length();
					if (dist <= ClusterMergeRadius)
					{
						cluster.Add(j);
						processed.Add(j);
					}
				}
				
				// Als we een cluster vonden (2+ punten), vervang door gemiddelde
				if (cluster.Count >= 2)
				{
					clustersFound++;
					Vector3 center = Vector3.Zero;
					foreach (int idx in cluster)
					{
						center += correctedPoints[idx];
					}
					center /= cluster.Count;
					
					// Verwijder alle punten in cluster (van achter naar voor)
					cluster.Sort();
					cluster.Reverse();
					foreach (int idx in cluster)
					{
						correctedPoints.RemoveAt(idx);
						clustersSmoothed++;
					}
					
					// Voeg centrum toe
					correctedPoints.Insert(cluster[cluster.Count - 1], center);
					
					changed = true;
					break; // Herstart de loop
				}
			}
		}
		
		GD.Print($"[AUTO CORRECT] Stap 1C: {clustersFound} clusters gevonden, {clustersSmoothed} punten vervangen door centrum (nu {correctedPoints.Count} punten)");
		
		// STAP 2: Snap hoogtes naar gemeenschappelijke niveaus
		// Groepeer punten per hoogte-niveau
		var heightGroups = new List<List<int>>();
		var assigned = new bool[correctedPoints.Count];
		
		for (int i = 0; i < correctedPoints.Count; i++)
		{
			if (assigned[i]) continue;
			
			var group = new List<int> { i };
			assigned[i] = true;
			
			for (int j = i + 1; j < correctedPoints.Count; j++)
			{
				if (assigned[j]) continue;
				
				float heightDiff = Mathf.Abs(correctedPoints[j].Y - correctedPoints[i].Y);
				if (heightDiff <= HeightSnapThreshold)
				{
					group.Add(j);
					assigned[j] = true;
				}
			}
			
			if (group.Count > 1)
			{
				heightGroups.Add(group);
			}
		}
		
		// Voor elke groep, bereken gemiddelde hoogte en pas toe
		int snappedCount = 0;
		foreach (var group in heightGroups)
		{
			float avgHeight = 0f;
			foreach (int idx in group)
			{
				avgHeight += correctedPoints[idx].Y;
			}
			avgHeight /= group.Count;
			
			// Snap alle punten in de groep naar deze hoogte
			foreach (int idx in group)
			{
				var pt = correctedPoints[idx];
				correctedPoints[idx] = new Vector3(pt.X, avgHeight, pt.Z);
				snappedCount++;
			}
		}
		
		GD.Print($"[AUTO CORRECT] Stap 2: {snappedCount} punten hoogte-gecorrigeerd in {heightGroups.Count} niveaus");
		
		// STAP 2B: Uniformeer hoogteverschillen tussen treden
		if (heightGroups.Count >= 2)
		{
			// Sorteer groepen op hoogte
			heightGroups.Sort((a, b) =>
			{
				float heightA = correctedPoints[a[0]].Y;
				float heightB = correctedPoints[b[0]].Y;
				return heightA.CompareTo(heightB);
			});
			
			// Bereken alle hoogteverschillen tussen opeenvolgende treden
			var heightDiffs = new List<float>();
			for (int i = 0; i < heightGroups.Count - 1; i++)
			{
				float heightCurrent = correctedPoints[heightGroups[i][0]].Y;
				float heightNext = correctedPoints[heightGroups[i + 1][0]].Y;
				float diff = heightNext - heightCurrent;
				if (diff > 0.01f) // Alleen echte treden (> 1cm verschil)
				{
					heightDiffs.Add(diff);
				}
			}
			
			if (heightDiffs.Count > 0)
			{
				// Bereken gemiddelde tredehoogte
				float avgStepHeight = 0f;
				foreach (float diff in heightDiffs)
				{
					avgStepHeight += diff;
				}
				avgStepHeight /= heightDiffs.Count;
				
				GD.Print($"[AUTO CORRECT] Stap 2B: Gemiddelde tredehoogte = {avgStepHeight:F3}m ({avgStepHeight * 100f:F1}cm)");
				
				// Herpositioneer alle treden met uniforme hoogteverschillen
				// Behoud de hoogte van de eerste (laagste) trede als referentie
				float baseHeight = correctedPoints[heightGroups[0][0]].Y;
				
				for (int i = 0; i < heightGroups.Count; i++)
				{
					float newHeight = baseHeight + (i * avgStepHeight);
					
					foreach (int idx in heightGroups[i])
					{
						var pt = correctedPoints[idx];
						correctedPoints[idx] = new Vector3(pt.X, newHeight, pt.Z);
					}
					
					if (i < 5) // Log eerste paar treden
					{
						GD.Print($"  Trede {i}: hoogte = {newHeight:F3}m");
					}
				}
				
				GD.Print($"[AUTO CORRECT] Stap 2B: {heightGroups.Count} treden geüniformeerd naar {avgStepHeight * 100f:F1}cm per trede");
			}
		}
		
		// STAP 3: Rond af op millimeters
		for (int i = 0; i < correctedPoints.Count; i++)
		{
			var pt = correctedPoints[i];
			correctedPoints[i] = new Vector3(
				Mathf.Round(pt.X * 1000f) / 1000f,
				Mathf.Round(pt.Y * 1000f) / 1000f,
				Mathf.Round(pt.Z * 1000f) / 1000f
			);
		}
		
		GD.Print($"[AUTO CORRECT] Stap 3: Afgerond op millimeters");
		GD.Print($"[AUTO CORRECT] Klaar! {_currentPoints.Count} → {correctedPoints.Count} punten");
		
		// Update de data en herbouw visualisatie
		_currentPoints = correctedPoints;
		ClearAllVisualizations();
		BuildPoints(_currentPoints);
		_currentCornerPoints = BuildLevelLines(_currentPoints);
		BuildCornerPoints(_currentCornerPoints);
		BuildStairSteps(_currentPoints, _currentCornerPoints);
		
		// Refresh edit points als we in edit mode zijn
		if (_editModeManager != null && IsEditMode())
		{
			_editModeManager.RefreshEditPoints();
		}
	}

	public override void _Process(double delta)
	{
		// Rebuild Now knop werkt alleen in de editor
		if (Engine.IsEditorHint() && RebuildNow)
		{
			RebuildNow = false;
			if (BuildInEditor)
			{
				Rebuild();
			}
			else
			{
				GD.Print("Zet 'Build In Editor' aan om Rebuild Now te gebruiken");
			}
		}
	}

	public void Rebuild()
	{
		if (!FileAccess.FileExists(CsvPath))
		{
			GD.PushError($"CSV niet gevonden: {CsvPath}");
			return;
		}

		var pts = LoadPointsFromCsv(CsvPath);
		_currentPoints = new List<Vector3>(pts);

		BuildPoints(_currentPoints);
		_currentCornerPoints = BuildLevelLines(_currentPoints);
		BuildCornerPoints(_currentCornerPoints);
		BuildStairSteps(_currentPoints, _currentCornerPoints);
	}

	public void LoadCsvFile(string path)
	{
		if (!FileAccess.FileExists(path))
		{
			GD.PushError($"CSV niet gevonden: {path}");
			return;
		}

		GD.Print($"Laden CSV: {path}");
		CsvPath = path;
		
		var pts = LoadPointsFromCsv(path);
		_currentPoints = new List<Vector3>(pts);

		// Clear oude visualisaties en rebuild met nieuwe data
		ClearAllVisualizations();
		BuildPoints(_currentPoints);
		_currentCornerPoints = BuildLevelLines(_currentPoints);
		BuildCornerPoints(_currentCornerPoints);
		BuildStairSteps(_currentPoints, _currentCornerPoints);
		
		GD.Print($"CSV geladen: {pts.Count} punten");
	}	// ---------- POINTS ----------
	private void BuildPoints(List<Vector3> pts)
	{
		if (pts.Count == 0) { Multimesh = null; return; }

		Mesh baseMesh = UseSpheres
			? new SphereMesh { Radius = PointSize * 0.5f, Height = PointSize, RadialSegments = 8, Rings = 4 }
			: new BoxMesh { Size = new Vector3(PointSize, PointSize, PointSize) };

		var mat = new StandardMaterial3D { AlbedoColor = PointColor };
		baseMesh.SurfaceSetMaterial(0, mat);

		var mm = new MultiMesh
		{
			Mesh = baseMesh,
			TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
			InstanceCount = pts.Count
		};

		for (int i = 0; i < pts.Count; i++)
			mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, pts[i]));

		Multimesh = mm;
	}

// ---------- CORNER POINTS ----------
private void BuildCornerPoints(List<Vector3> cornerPoints)
{
	// Verwijder oude corner points
	var old = GetNodeOrNull<MultiMeshInstance3D>("CornerPoints");
	if (old != null) old.QueueFree();

	if (!DrawCornerPoints || cornerPoints.Count == 0) return;

	Mesh baseMesh = UseSpheres
		? new SphereMesh { Radius = CornerPointSize * 0.5f, Height = CornerPointSize, RadialSegments = 8, Rings = 4 }
		: new BoxMesh { Size = new Vector3(CornerPointSize, CornerPointSize, CornerPointSize) };

	var mat = new StandardMaterial3D { AlbedoColor = CornerPointColor };
	baseMesh.SurfaceSetMaterial(0, mat);

	var mm = new MultiMesh
	{
		Mesh = baseMesh,
		TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
		InstanceCount = cornerPoints.Count
	};

	for (int i = 0; i < cornerPoints.Count; i++)
		mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, cornerPoints[i]));

	var cornerMmi = new MultiMeshInstance3D
	{
		Name = "CornerPoints",
		Multimesh = mm
	};
	
	AddChild(cornerMmi);
	
	GD.Print($"[CORNER POINTS] {cornerPoints.Count} corner points getekend");
}

// ---------- LEVEL LINES ----------
private List<Vector3> BuildLevelLines(List<Vector3> pts)
{
	var cornerPoints = BuildLevelLinesInternal(pts);
	_currentCornerPoints = new List<Vector3>(cornerPoints);
	return cornerPoints;
}

private void BuildLevelLinesFromData(List<Vector3> pts, List<Vector3> existingCornerPoints)
{
	// Gebruik bestaande corner points indien beschikbaar
	if (existingCornerPoints != null && existingCornerPoints.Count > 0)
	{
		BuildLevelLinesInternal(pts, existingCornerPoints);
	}
	else
	{
		BuildLevelLinesInternal(pts);
	}
}

private List<Vector3> BuildLevelLinesInternal(List<Vector3> pts, List<Vector3> predefinedCornerPoints = null)
{
	// oude bars weg
	var old = GetNodeOrNull<MultiMeshInstance3D>("LevelBars");
	if (old != null) old.QueueFree();

	if (!DrawLevelLines || pts.Count < 2) return new List<Vector3>();
	
	// Als debug mode UIT is, teken geen groene lijnen maar sla wel corner points op
	if (!_debugMode)
	{
		// Bereken corner points maar teken ze niet
		var tempCornerPoints = predefinedCornerPoints ?? new List<Vector3>();
		bool hasExistingCorners = (predefinedCornerPoints != null && predefinedCornerPoints.Count > 0);
		
		if (!hasExistingCorners)
		{
			// Bereken corner points voor trede-vulling
			float heightThresh = 0.05f;
			for (int i = 0; i < pts.Count - 1; i++)
			{
				Vector3 a = pts[i];
				Vector3 b = pts[i + 1];
				float dist3D = (b - a).Length();
				float dist2D = new Vector2(b.X - a.X, b.Z - a.Z).Length();
				float heightDiff = Mathf.Abs(b.Y - a.Y);
				bool goingUp = b.Y > a.Y;
				
				if (dist3D <= MaxRowGap && heightDiff > heightThresh && dist2D > 0.05f)
				{
					Vector3 cornerPoint = goingUp 
						? new Vector3(b.X, a.Y, b.Z)
						: new Vector3(a.X, b.Y, a.Z);
					tempCornerPoints.Add(cornerPoint);
				}
			}
		}
		
		return tempCornerPoints;
	}

	var segments = new List<(Vector3 a, Vector3 b)>();

	// ===== SEQUENTIËLE VERBINDINGSSTRATEGIE MET RECHTE HOEKEN =====
	// De CSV punten zijn gemeten in volgorde: rechts omhoog, dan links omlaag
	// Bij verticale beweging: voeg tussenpunt toe voor rechte hoek
	// OMHOOG: horizontaal dan verticaal | OMLAAG: verticaal dan horizontaal
	
	GD.Print($"[SEQUENTIEEL] Verbind {pts.Count} punten in CSV volgorde met rechte hoeken");
	GD.Print($"  Eerste punt: ({pts[0].X:F2}, {pts[0].Y:F2}, {pts[0].Z:F2})");
	GD.Print($"  Laatste punt: ({pts[pts.Count-1].X:F2}, {pts[pts.Count-1].Y:F2}, {pts[pts.Count-1].Z:F2})");
	
	int connectedCount = 0;
	int skippedCount = 0;
	int cornerPointsUp = 0;
	int cornerPointsDown = 0;
	float heightThreshold = 0.05f; // Als hoogteverschil > 5cm, maak rechte hoek
	
	// Lijst om tussenpunten op te slaan voor laterale verbindingen
	var cornerPoints = predefinedCornerPoints ?? new List<Vector3>();
	bool useExistingCorners = (predefinedCornerPoints != null && predefinedCornerPoints.Count > 0);
	Vector3? previousCorner = null;
	
	for (int i = 0; i < pts.Count - 1; i++)
	{
		Vector3 a = pts[i];
		Vector3 b = pts[i + 1];
		
		// Bereken afstand en hoogteverschil
		float dist3D = (b - a).Length();
		float dist2D = new Vector2(b.X - a.X, b.Z - a.Z).Length();
		float heightDiff = Mathf.Abs(b.Y - a.Y);
		bool goingUp = b.Y > a.Y; // Check of we omhoog of omlaag gaan
		
		// Skip als te ver weg
		if (dist3D > MaxRowGap)
		{
			skippedCount++;
			GD.Print($"  ⚠ Skip grote sprong tussen punt {i} en {i+1}: {dist3D:F2}m (2D: {dist2D:F2}m)");
			continue;
		}
		
		// Check of er zowel horizontale als verticale beweging is
		if (heightDiff > heightThreshold && dist2D > 0.05f)
		{
			Vector3 cornerPoint;
			
			if (goingUp)
			{
				// OMHOOG: eerst horizontaal (op hoogte van A), dan verticaal
				cornerPoint = new Vector3(b.X, a.Y, b.Z);
				
				segments.Add((a, cornerPoint));      // Horizontaal
				segments.Add((cornerPoint, b));      // Verticaal omhoog
				
				cornerPointsUp++;
				if (cornerPointsUp <= 3)
				{
					GD.Print($"  ↗ Omhoog {cornerPointsUp}: punt {i}→{i+1}, hoogte +{b.Y - a.Y:F3}m");
				}
			}
			else
			{
				// OMLAAG: eerst verticaal (omlaag naar hoogte van B), dan horizontaal
				cornerPoint = new Vector3(a.X, b.Y, a.Z);
				
				segments.Add((a, cornerPoint));      // Verticaal omlaag
				segments.Add((cornerPoint, b));      // Horizontaal
				
				cornerPointsDown++;
				if (cornerPointsDown <= 3)
				{
					GD.Print($"  ↘ Omlaag {cornerPointsDown}: punt {i}→{i+1}, hoogte -{a.Y - b.Y:F3}m");
				}
			}
			
		// Verbind met vorige corner point als die er is
		if (previousCorner.HasValue)
		{
			segments.Add((previousCorner.Value, cornerPoint));
		}
		
		previousCorner = cornerPoint;
		if (!useExistingCorners)
		{
			cornerPoints.Add(cornerPoint);
		}
		connectedCount += 2;
		}
		else
		{
			// Gewone directe verbinding (geen significante hoogte verandering)
			segments.Add((a, b));
			connectedCount++;
		}
	}GD.Print($"[RESULTAAT] Sequentieel: {connectedCount} lijnen, {cornerPointsUp} omhoog, {cornerPointsDown} omlaag, {skippedCount} overgeslagen");
GD.Print($"[CORNER VERBINDINGEN] {cornerPoints.Count} tussenpunten");

// ===== DWARSVERBINDINGEN TUSSEN CORNER POINTS =====
// Verbind corner points op hetzelfde hoogte-niveau met elkaar
GD.Print($"[CORNER DWARS] Zoek corner points op zelfde hoogte...");

int cornerCrossConnections = 0;

for (int i = 0; i < cornerPoints.Count; i++)
{
	Vector3 cornerA = cornerPoints[i];
	
	// Zoek andere corner points op vergelijkbaar hoogte-niveau
	for (int j = i + 1; j < cornerPoints.Count; j++)
	{
		Vector3 cornerB = cornerPoints[j];
		
		float heightDiff = Mathf.Abs(cornerB.Y - cornerA.Y);
		
		// Als ze op hetzelfde hoogte-niveau zitten
		if (heightDiff <= LevelEpsilon)
		{
			float dist2D = new Vector2(cornerB.X - cornerA.X, cornerB.Z - cornerA.Z).Length();
			
			// Verbind als afstand redelijk is
			if (dist2D > 0.15f && dist2D <= MaxRowGap)
			{
				segments.Add((cornerA, cornerB));
				cornerCrossConnections++;
				
				if (cornerCrossConnections <= 5)
				{
					GD.Print($"  Corner dwars {cornerCrossConnections}: Y={cornerA.Y:F3}m, afstand={dist2D:F2}m");
				}
			}
		}
	}
}

GD.Print($"[RESULTAAT] Corner dwarsverbindingen: {cornerCrossConnections} lijnen");

	
// ===== DWARSVERBINDINGEN (TREDEN) =====
// Verbind punten op dezelfde hoogte aan linker en rechter kant
// Slimmere aanpak: groepeer eerst per hoogte, verbind dan binnen elke groep	GD.Print($"[DWARSVERBINDINGEN] Zoek punten op zelfde hoogte...");
	
	// Groepeer punten per hoogte niveau
	var heightGroups = new List<List<(int index, Vector3 point)>>();
	var usedPoints = new bool[pts.Count];
	
	for (int i = 0; i < pts.Count; i++)
	{
		if (usedPoints[i]) continue;
		
		var group = new List<(int, Vector3)> { (i, pts[i]) };
		usedPoints[i] = true;
		
		// Vind alle andere punten op vergelijkbare hoogte
		for (int j = i + 1; j < pts.Count; j++)
		{
			if (usedPoints[j]) continue;
			
			float heightDiff = Mathf.Abs(pts[j].Y - pts[i].Y);
			if (heightDiff <= LevelEpsilon)
			{
				group.Add((j, pts[j]));
				usedPoints[j] = true;
			}
		}
		
		if (group.Count > 1)
		{
			heightGroups.Add(group);
		}
	}
	
	GD.Print($"  Gevonden {heightGroups.Count} hoogte-niveaus met meerdere punten");
	
	int crossConnections = 0;
	
	// Voor elke hoogte-groep, maak dwarsverbindingen
	foreach (var group in heightGroups)
	{
		if (group.Count < 2) continue;
		
		// Sorteer punten in de groep op CSV index (volgorde van meten)
		group.Sort((a, b) => a.index.CompareTo(b.index));
		
		// Verbind het eerste punt met het laatste punt in de groep
		// (dit is meestal links naar rechts of vice versa)
		var first = group[0];
		var last = group[group.Count - 1];
		
		float dist2D = new Vector2(last.point.X - first.point.X, last.point.Z - first.point.Z).Length();
		
	// Alleen toevoegen als de afstand redelijk is
	if (dist2D > 0.15f && dist2D <= MaxRowGap)
	{
		segments.Add((first.point, last.point));
		crossConnections++;
		
		if (crossConnections <= 8)
		{
			GD.Print($"  Trede {crossConnections}: Y={first.point.Y:F3}m, {group.Count} punten, breedte={dist2D:F2}m (punt {first.index} → {last.index})");
		}
	}
	
	// EXTRA: Verbind ALLE punten op dit niveau met elkaar (niet alleen eerste-laatste)
	// Dit zorgt voor volledige horizontale dekking
	if (group.Count >= 2)
	{
		for (int i = 0; i < group.Count; i++)
		{
			for (int j = i + 1; j < group.Count; j++)
			{
				var a = group[i];
				var b = group[j];
				
				float d2D = new Vector2(b.point.X - a.point.X, b.point.Z - a.point.Z).Length();
				
				// Verbind als afstand tussen min en max ligt
				if (d2D > 0.15f && d2D <= MaxRowGap)
				{
					// Check of deze lijn niet al bestaat (eerste-laatste is al toegevoegd)
					bool isDuplicate = (i == 0 && j == group.Count - 1);
					
					if (!isDuplicate)
					{
						segments.Add((a.point, b.point));
						crossConnections++;
					}
				}
			}
		}
	}
}
GD.Print($"[RESULTAAT] Dwarsverbindingen: {crossConnections} lijnen toegevoegd");
GD.Print($"[TOTAAL] {segments.Count} segmenten voor rendering");

if (segments.Count == 0)
{
	GD.Print("[WAARSCHUWING] Geen segmenten gevonden!");
	return new List<Vector3>();
}

GD.Print($"[TOTAAL] {segments.Count} segmenten voor deduplicatie");

	// STAP 4: Deduplicatie - verwijder dubbele lijnen
	var uniqueSegs = new HashSet<(long, long)>();
	var finalSegs = new List<(Vector3 a, Vector3 b)>();
	
	long HashPoint(Vector3 v)
	{
		long xi = (long)Mathf.Round(v.X * 10000f);
		long yi = (long)Mathf.Round(v.Y * 10000f);
		long zi = (long)Mathf.Round(v.Z * 10000f);
		return (xi & 0x1FFFFF) | ((yi & 0x1FFFFF) << 21) | ((zi & 0x1FFFFF) << 42);
	}
	
	foreach (var seg in segments)
	{
		long ha = HashPoint(seg.a);
		long hb = HashPoint(seg.b);
		var key = ha <= hb ? (ha, hb) : (hb, ha);
		
		if (uniqueSegs.Add(key))
		{
			finalSegs.Add(seg);
		}
	}

	GD.Print($"[RENDER] Tekenen van {finalSegs.Count} unieke lijnen");

	// STAP 5: Render alle lijnen met dynamische lengte
	// Gebruik BoxMesh die langs X-as geschaald wordt (simpeler en betrouwbaarder)
	var boxMesh = new BoxMesh 
	{ 
		Size = new Vector3(1.0f, LineThickness, LineThickness)
	};
	
	var material = new StandardMaterial3D 
	{ 
		AlbedoColor = LineColor,
		ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel 
	};
	boxMesh.SurfaceSetMaterial(0, material);

	var multiMesh = new MultiMesh
	{
		Mesh = boxMesh,
		TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
		InstanceCount = finalSegs.Count
	};

	int validLines = 0;
	for (int i = 0; i < finalSegs.Count; i++)
	{
		var (pointA, pointB) = finalSegs[i];
		
		// Bereken richting en lengte DIRECT tussen de punten
		Vector3 delta = pointB - pointA;
		float length = delta.Length();
		
		if (length < 0.0001f)
		{
			// Verstop te korte lijnen
			multiMesh.SetInstanceTransform(i, new Transform3D(Basis.Identity, new Vector3(0, -1000, 0)));
			continue;
		}
		
		validLines++;
		
		// Debug: print lengte van enkele lijnen
		if (i < 5)
		{
			GD.Print($"  Lijn {i}: A=({pointA.X:F3},{pointA.Y:F3},{pointA.Z:F3}) B=({pointB.X:F3},{pointB.Y:F3},{pointB.Z:F3}) Lengte={length:F3}m");
		}
		
		// Middelpunt
		Vector3 midpoint = pointA + delta * 0.5f;
		
		// Normaliseer de richting
		Vector3 forward = delta / length;
		
		// Maak een LookAt-achtige basis
		// Forward (X-as) = richting van de lijn
		// Up proberen te behouden, maar perpendiculair maken
		Vector3 up = Vector3.Up;
		
		// Als forward bijna parallel is aan Up, gebruik een andere up vector
		if (Mathf.Abs(forward.Dot(up)) > 0.99f)
		{
			up = Vector3.Right;
		}
		
		// Right (Z-as) = forward × up
		Vector3 right = forward.Cross(up).Normalized();
		
		// Up (Y-as) = right × forward (nu gegarandeerd orthogonaal)
		up = right.Cross(forward).Normalized();
		
		// Bouw basis: kolom 0 = X (forward), kolom 1 = Y (up), kolom 2 = Z (right)
		var basis = new Basis(forward, up, right);
		
		// Schaal ALLEEN de X-as met de lengte
		var transform = new Transform3D(basis, Vector3.Zero);
		transform = transform.ScaledLocal(new Vector3(length, 1.0f, 1.0f));
		transform.Origin = midpoint;
		
		multiMesh.SetInstanceTransform(i, transform);
	}
	
	GD.Print($"[RENDER] {validLines} geldige lijnen van {finalSegs.Count} totaal");

	AddChild(new MultiMeshInstance3D { Name = "LevelBars", Multimesh = multiMesh });
	
	return cornerPoints;
}

// ---------- STAIR STEPS (GEVULDE TREDEN) ----------
private void BuildStairSteps(List<Vector3> pts, List<Vector3> cornerPoints)
{
	// Verwijder oude treden
	var old = GetNodeOrNull<Node3D>("StairSteps");
	if (old != null) old.QueueFree();

	if (!DrawStairSteps) return;

	GD.Print($"[TREDEN] Begin met maken van gevulde treden uit {pts.Count} punten + {cornerPoints.Count} corner punten...");

	var stepsParent = new Node3D { Name = "StairSteps" };
	AddChild(stepsParent);

	// Combineer alle punten: originele CSV punten + corner points
	var allPoints = new List<Vector3>();
	allPoints.AddRange(pts);
	allPoints.AddRange(cornerPoints);
	
	if (allPoints.Count < 3) return;

	// Groepeer alle punten per hoogte niveau
	var heightGroups = new List<List<(int index, Vector3 point)>>();
	var usedPoints = new bool[allPoints.Count];

	for (int i = 0; i < allPoints.Count; i++)
	{
		if (usedPoints[i]) continue;

		var group = new List<(int, Vector3)> { (i, allPoints[i]) };
		usedPoints[i] = true;

		for (int j = i + 1; j < allPoints.Count; j++)
		{
			if (usedPoints[j]) continue;

			float heightDiff = Mathf.Abs(allPoints[j].Y - allPoints[i].Y);
			if (heightDiff <= LevelEpsilon)
			{
				group.Add((j, allPoints[j]));
				usedPoints[j] = true;
			}
		}

		if (group.Count >= 3) // Minimaal 3 punten nodig voor een gevuld vlak
		{
			heightGroups.Add(group);
		}
	}

	GD.Print($"[TREDEN] Gevonden {heightGroups.Count} hoogte-niveaus met >=3 punten");

	int stepCount = 0;

	// Voor elke hoogte-groep, maak een horizontaal trede-vlak
	foreach (var group in heightGroups)
	{
		if (group.Count < 3) continue;

		var surfaceTool = new SurfaceTool();
		surfaceTool.Begin(Mesh.PrimitiveType.Triangles);

		// Materiaal
		var material = new StandardMaterial3D
		{
			AlbedoColor = StepColor,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled // Beide kanten zichtbaar
		};

		// Normale vector is altijd omhoog voor een horizontale trede
		var normal = Vector3.Up;
		
		// Bereken het centrum van alle punten
		Vector3 center = Vector3.Zero;
		foreach (var p in group)
		{
			center += p.point;
		}
		center /= group.Count;
		
		// Sorteer punten op hoek vanaf het centrum (in XZ vlak)
		var sortedPoints = new List<Vector3>();
		foreach (var p in group)
		{
			sortedPoints.Add(p.point);
		}
		
		sortedPoints.Sort((a, b) =>
		{
			float angleA = Mathf.Atan2(a.Z - center.Z, a.X - center.X);
			float angleB = Mathf.Atan2(b.Z - center.Z, b.X - center.X);
			return angleA.CompareTo(angleB);
		});
		
		// Gebruik fan triangulatie vanaf het centrum
		for (int i = 0; i < sortedPoints.Count; i++)
		{
			int nextI = (i + 1) % sortedPoints.Count;
			
			surfaceTool.SetNormal(normal);
			surfaceTool.AddVertex(center);
			surfaceTool.AddVertex(sortedPoints[i]);
			surfaceTool.AddVertex(sortedPoints[nextI]);
		}

		var mesh = surfaceTool.Commit();
		mesh.SurfaceSetMaterial(0, material);

		var meshInstance = new MeshInstance3D
		{
			Mesh = mesh,
			Name = $"Step_{stepCount}"
		};

		stepsParent.AddChild(meshInstance);
		stepCount++;
		
		if (stepCount <= 10)
		{
			GD.Print($"  Trede {stepCount}: {group.Count} punten op Y={group[0].point.Y:F3}m");
		}
	}

	GD.Print($"[TREDEN] {stepCount} treden aangemaakt");
}

	// CSV: kolommen X,Y,Z waarbij Z (csv) = hoogte → Godot Y
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

		if (!float.TryParse(parts[0].Trim().Trim('"'), NumberStyles.Float, _inv, out var x)) continue;
		if (!float.TryParse(parts[1].Trim().Trim('"'), NumberStyles.Float, _inv, out var y)) continue;
	if (!float.TryParse(parts[2].Trim().Trim('"'), NumberStyles.Float, _inv, out var z)) continue;

		// Z uit CSV is hoogte → Godot Y
		// Rond af op 2 decimalen
		float x2 = Mathf.Round(x * 100f) / 100f;
		float y2 = Mathf.Round(y * 100f) / 100f;
		float z2 = Mathf.Round(z * 100f) / 100f;
		
		pts.Add(new Vector3(x2, z2, y2));
	}		
	return pts;
	}
}
