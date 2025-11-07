using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Beheert edit mode: versleepbare punten, camera raycasting, en realtime mesh updates
/// </summary>
public partial class EditModeManager : Node3D
{
	private CsvPointPlacer _pointPlacer;
	private Camera3D _camera;
	private Node3D _editPointsParent;
	private List<EditablePoint> _editablePoints = new List<EditablePoint>();
	
	private EditablePoint _selectedPoint = null;
	private EditablePoint _hoveredPoint = null;
	private bool _isDragging = false;
	private Plane _dragPlane;
	private Vector3 _dragOffset;
	private float _lockedYHeight; // Vergrendelde hoogte tijdens drag
	private List<EditablePoint> _linkedPoints = new List<EditablePoint>(); // Punten die mee moeten bewegen
	
	// Voor het spawnen van nieuwe punten op lijnen
	private List<(Vector3 start, Vector3 end, int startIndex, int endIndex)> _lineSegments = new List<(Vector3, Vector3, int, int)>();
	
	// Confirmation dialog voor nieuwe punten
	private ConfirmationDialog _spawnConfirmDialog;
	private Vector3 _pendingSpawnPosition;
	private int _pendingInsertIndex;

	public bool IsEditMode { get; private set; } = false;

	public override void _Ready()
	{
		_pointPlacer = GetParent<CsvPointPlacer>();
		_camera = GetViewport().GetCamera3D();
		
		_editPointsParent = new Node3D { Name = "EditPoints" };
		AddChild(_editPointsParent);
		
		// Maak confirmation dialog aan voor spawning
		CreateSpawnConfirmDialog();
	}
	
	private void CreateSpawnConfirmDialog()
	{
		_spawnConfirmDialog = new ConfirmationDialog();
		_spawnConfirmDialog.Title = "Nieuw Punt Toevoegen";
		_spawnConfirmDialog.DialogText = "Wil je een nieuw punt op deze positie toevoegen?";
		_spawnConfirmDialog.OkButtonText = "Confirm";
		_spawnConfirmDialog.CancelButtonText = "Cancel";
		_spawnConfirmDialog.Size = new Vector2I(450, 200);
		
		// Connect signals
		_spawnConfirmDialog.Confirmed += OnSpawnConfirmed;
		_spawnConfirmDialog.Canceled += OnSpawnCanceled;
		
		// Voeg toe aan scene tree
		AddChild(_spawnConfirmDialog);
	}
	
	private void OnSpawnConfirmed()
	{
		GD.Print($"[EDIT MODE] ✓ Bevestigd: Spawn nieuw punt op positie {_pendingSpawnPosition} na index {_pendingInsertIndex}");
		
		// Save undo state
		_pointPlacer.SaveEditUndoState();
		
		// Voeg nieuw punt toe aan de point placer
		_pointPlacer.InsertPoint(_pendingInsertIndex + 1, _pendingSpawnPosition);
		
		// Rebuild visualisatie
		_pointPlacer.RebuildFromCurrentData();
		
		// Refresh edit points
		RefreshEditPoints();
	}
	
	private void OnSpawnCanceled()
	{
		GD.Print("[EDIT MODE] ✗ Geannuleerd: Nieuw punt niet toegevoegd");
	}

	public void ToggleEditMode()
	{
		IsEditMode = !IsEditMode;
		
		if (IsEditMode)
		{
			EnterEditMode();
		}
		else
		{
			ExitEditMode();
		}
		
		GD.Print($"[EDIT MODE] {(IsEditMode ? "ENABLED" : "DISABLED")}");
	}

	private void EnterEditMode()
	{
		// Maak editable points voor alle CSV punten en corner punten
		ClearEditPoints();
		
		// Zet muis vrij voor edit mode
		Input.MouseMode = Input.MouseModeEnum.Visible;
		
		// Haal punten op uit de CsvPointPlacer
		var csvPoints = _pointPlacer.GetPoints();
		var cornerPoints = _pointPlacer.GetCornerPoints();
		
		GD.Print($"[EDIT MODE] Creating {csvPoints.Count} CSV points + {cornerPoints.Count} corner points");
		
		// CSV punten
		for (int i = 0; i < csvPoints.Count; i++)
		{
			var editPoint = new EditablePoint
			{
				OriginalPosition = csvPoints[i],
				PointIndex = i,
				IsCornerPoint = false,
				Position = csvPoints[i]
			};
			_editPointsParent.AddChild(editPoint);
			_editablePoints.Add(editPoint);
		}
		
		// Corner punten
		for (int i = 0; i < cornerPoints.Count; i++)
		{
			var editPoint = new EditablePoint
			{
				OriginalPosition = cornerPoints[i],
				PointIndex = csvPoints.Count + i,
				IsCornerPoint = true,
				Position = cornerPoints[i]
			};
			_editPointsParent.AddChild(editPoint);
			_editablePoints.Add(editPoint);
		}
		
		// Bouw lijn segmenten voor click detectie
		BuildLineSegments();
	}

	private void ExitEditMode()
	{
		_isDragging = false;
		_selectedPoint = null;
		_hoveredPoint = null;
		_lineSegments.Clear();
		ClearEditPoints();
	}
	
	private void BuildLineSegments()
	{
		_lineSegments.Clear();
		
		var csvPoints = _pointPlacer.GetPoints();
		var cornerPoints = _pointPlacer.GetCornerPoints();
		
		if (csvPoints.Count < 2) return;
		
		// Bouw lijn segmenten gebaseerd op dezelfde logica als BuildLevelLines
		// We moeten de verbindingen tussen CSV punten en corner punten reconstrueren
		
		int cornerIndex = 0;
		float heightThreshold = 0.05f; // Zelfde als in BuildLevelLines
		float maxGap = 4.0f; // MaxRowGap van CsvPointPlacer
		
		for (int i = 0; i < csvPoints.Count - 1; i++)
		{
			Vector3 a = csvPoints[i];
			Vector3 b = csvPoints[i + 1];
			
			float dist3D = (b - a).Length();
			float dist2D = new Vector2(b.X - a.X, b.Z - a.Z).Length();
			float heightDiff = Mathf.Abs(b.Y - a.Y);
			
			// Skip als te ver weg
			if (dist3D > maxGap) continue;
			
			// Check of er een corner point tussen zit
			if (heightDiff > heightThreshold && dist2D > 0.05f && cornerIndex < cornerPoints.Count)
			{
				Vector3 cornerPoint = cornerPoints[cornerIndex];
				
				// Voeg twee segmenten toe: a -> corner en corner -> b
				_lineSegments.Add((a, cornerPoint, i, -1)); // -1 betekent corner point
				_lineSegments.Add((cornerPoint, b, -1, i + 1));
				
				cornerIndex++;
			}
			else
			{
				// Directe verbinding zonder corner point
				_lineSegments.Add((a, b, i, i + 1));
			}
		}
		
		GD.Print($"[EDIT MODE] {_lineSegments.Count} line segments gebouwd voor click detectie (inclusief corners)");
	}

	private void ClearEditPoints()
	{
		foreach (var point in _editablePoints)
		{
			point.QueueFree();
		}
		_editablePoints.Clear();
	}
	
	// Public methode om edit points te refreshen na undo/redo
	public void RefreshEditPoints()
	{
		if (!IsEditMode) return;
		
		GD.Print("[EDIT MODE] Refreshing edit points na undo/redo");
		
		// Bewaar selectie state
		int? selectedIndex = null;
		bool wasCorner = false;
		if (_selectedPoint != null)
		{
			selectedIndex = _selectedPoint.PointIndex;
			wasCorner = _selectedPoint.IsCornerPoint;
		}
		
		// Verwijder oude punten
		ClearEditPoints();
		
		// Maak nieuwe punten op de juiste posities
		var csvPoints = _pointPlacer.GetPoints();
		var cornerPoints = _pointPlacer.GetCornerPoints();
		
		// CSV punten
		for (int i = 0; i < csvPoints.Count; i++)
		{
			var editPoint = new EditablePoint
			{
				OriginalPosition = csvPoints[i],
				PointIndex = i,
				IsCornerPoint = false,
				Position = csvPoints[i]
			};
			_editPointsParent.AddChild(editPoint);
			_editablePoints.Add(editPoint);
			
			// Herstel selectie
			if (selectedIndex.HasValue && !wasCorner && i == selectedIndex.Value)
			{
				_selectedPoint = editPoint;
				editPoint.SetSelected(true);
			}
		}
		
		// Corner punten
		for (int i = 0; i < cornerPoints.Count; i++)
		{
			var editPoint = new EditablePoint
			{
				OriginalPosition = cornerPoints[i],
				PointIndex = csvPoints.Count + i,
				IsCornerPoint = true,
				Position = cornerPoints[i]
			};
			_editPointsParent.AddChild(editPoint);
			_editablePoints.Add(editPoint);
			
			// Herstel selectie
			if (selectedIndex.HasValue && wasCorner && (csvPoints.Count + i) == selectedIndex.Value)
			{
				_selectedPoint = editPoint;
				editPoint.SetSelected(true);
			}
		}
		
		GD.Print($"[EDIT MODE] {csvPoints.Count} CSV points + {cornerPoints.Count} corner points refreshed");
		
		// Herbouw lijn segmenten
		BuildLineSegments();
	}

	public override void _Process(double delta)
	{
		if (!IsEditMode || _camera == null) return;

		// Raycast naar muis positie
		var mousePos = GetViewport().GetMousePosition();
		var from = _camera.ProjectRayOrigin(mousePos);
		var to = from + _camera.ProjectRayNormal(mousePos) * 1000f;

		var spaceState = GetWorld3D().DirectSpaceState;
		var query = PhysicsRayQueryParameters3D.Create(from, to);
		query.CollideWithBodies = true;
		
		var result = spaceState.IntersectRay(query);

		// Update hover state
		EditablePoint newHovered = null;
		if (result.Count > 0 && result["collider"].Obj is EditablePoint ep)
		{
			newHovered = ep;
		}

		if (newHovered != _hoveredPoint)
		{
			if (_hoveredPoint != null && _hoveredPoint != _selectedPoint)
			{
				_hoveredPoint.SetHovered(false);
			}
			_hoveredPoint = newHovered;
			if (_hoveredPoint != null && _hoveredPoint != _selectedPoint)
			{
				_hoveredPoint.SetHovered(true);
			}
		}

		// Update drag positie
		if (_isDragging && _selectedPoint != null)
		{
			var planeIntersect = _dragPlane.IntersectsRay(from, _camera.ProjectRayNormal(mousePos));
			if (planeIntersect.HasValue)
			{
				var newPos = planeIntersect.Value + _dragOffset;
				
				// VERGRENDEL Y-HOOGTE: alleen XZ beweging toegestaan
				newPos.Y = _lockedYHeight;
				
				// Bereken hoeveel het geselecteerde punt is verplaatst
				Vector3 deltaMove = newPos - _selectedPoint.Position;
				
				// Update het geselecteerde punt
				_selectedPoint.Position = newPos;
				UpdatePointInPlacer(_selectedPoint, newPos);
				
				// Update ALLE linked points met dezelfde delta
				foreach (var linkedPoint in _linkedPoints)
				{
					Vector3 linkedNewPos = linkedPoint.Position + deltaMove;
					linkedNewPos.Y = linkedPoint.Position.Y; // Behoud hun eigen hoogte
					linkedPoint.Position = linkedNewPos;
					UpdatePointInPlacer(linkedPoint, linkedNewPos);
				}
			}
		}
	}

	public override void _Input(InputEvent @event)
	{
		if (!IsEditMode) return;

		// Linker muisklik: selecteer/start drag OF spawn nieuw punt op lijn
		if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
		{
			if (mb.Pressed)
			{
				if (_hoveredPoint != null)
				{
					// Selecteer punt en start drag
					if (_selectedPoint != null)
					{
						_selectedPoint.SetSelected(false);
					}
					
					_selectedPoint = _hoveredPoint;
					_selectedPoint.SetSelected(true);
					_isDragging = true;
					
					// SAVE UNDO STATE voordat we beginnen met slepen
					_pointPlacer.SaveEditUndoState();
					GD.Print("[EDIT MODE] Undo state opgeslagen voor drag actie");

					// Vind alle linked points (punten die op dezelfde hoogte zitten)
					FindLinkedPoints(_selectedPoint);

					// Sla de originele Y-hoogte op (vergrendel hoogte)
					_lockedYHeight = _selectedPoint.GlobalPosition.Y;

					// Maak een HORIZONTALE drag plane op de hoogte van het punt
					// Normaal is altijd omhoog (Y-as), zodat we alleen XZ kunnen slepen
					_dragPlane = new Plane(Vector3.Up, _selectedPoint.GlobalPosition);
					
					// Bereken offset tussen raaycast hit en punt centrum
					var mousePos = GetViewport().GetMousePosition();
					var from = _camera.ProjectRayOrigin(mousePos);
					var rayDir = _camera.ProjectRayNormal(mousePos);
					var intersect = _dragPlane.IntersectsRay(from, rayDir);
					
					if (intersect.HasValue)
					{
						_dragOffset = _selectedPoint.GlobalPosition - intersect.Value;
					}
				}
				else
				{
					// Geen punt gehovered - check of we op een lijn klikken
					TrySpawnPointOnLine();
				}
			}
			else
			{
				// Stop drag
				_isDragging = false;
				_linkedPoints.Clear(); // Clear linked points na drag
			}
		}

		// ESC: deselect
		if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
		{
			if (_selectedPoint != null)
			{
				_selectedPoint.SetSelected(false);
				_selectedPoint = null;
				_linkedPoints.Clear();
			}
		}
	}

	private void FindLinkedPoints(EditablePoint selectedPoint)
	{
		_linkedPoints.Clear();
		
		float positionTolerance = 0.05f; // 5cm tolerance voor XZ positie vergelijking
		
		foreach (var point in _editablePoints)
		{
			if (point == selectedPoint) continue;
			
			// Check of punten VERTICAAL boven elkaar staan (zelfde X en Z)
			float xDiff = Mathf.Abs(point.Position.X - selectedPoint.Position.X);
			float zDiff = Mathf.Abs(point.Position.Z - selectedPoint.Position.Z);
			
			// Als X en Z bijna hetzelfde zijn, dan staan ze verticaal boven elkaar
			if (xDiff <= positionTolerance && zDiff <= positionTolerance)
			{
				_linkedPoints.Add(point);
				GD.Print($"[LINKED] Verticaal gekoppeld punt: Y={point.Position.Y:F2}m (selected Y={selectedPoint.Position.Y:F2}m)");
			}
		}
		
		GD.Print($"[DRAG] {_linkedPoints.Count} verticaal gekoppelde punten gevonden");
	}

	private void UpdatePointInPlacer(EditablePoint editPoint, Vector3 newPosition)
	{
		if (editPoint.IsCornerPoint)
		{
			// Update corner point
			int cornerIndex = editPoint.PointIndex - _pointPlacer.GetPoints().Count;
			_pointPlacer.UpdateCornerPoint(cornerIndex, newPosition);
		}
		else
		{
			// Update CSV point
			_pointPlacer.UpdatePoint(editPoint.PointIndex, newPosition);
		}
		
		// Rebuild de mesh realtime
		_pointPlacer.RebuildFromCurrentData();
	}
	
	private void TrySpawnPointOnLine()
	{
		GD.Print("[EDIT MODE] TrySpawnPointOnLine aangeroepen");
		
		// Raycast naar de muispositie
		var mousePos = GetViewport().GetMousePosition();
		var from = _camera.ProjectRayOrigin(mousePos);
		var rayDir = _camera.ProjectRayNormal(mousePos);
		
		GD.Print($"[EDIT MODE] Mouse pos: {mousePos}, Ray from: {from}, Ray dir: {rayDir}");
		GD.Print($"[EDIT MODE] Checking {_lineSegments.Count} line segments");
		
		// Vind het dichtstbijzijnde punt op een lijn segment
		Vector3? closestPointOnLine = null;
		int insertAfterIndex = -1;
		float closestDistance = float.MaxValue;
		float maxClickDistance = 0.5f; // Verhoogd naar 50cm voor makkelijker testen
		
		for (int i = 0; i < _lineSegments.Count; i++)
		{
			var segment = _lineSegments[i];
			
			// Bereken het dichtstbijzijnde punt op dit lijn segment bij de ray
			var closestOnSegment = ClosestPointOnLineSegmentToRay(segment.start, segment.end, from, rayDir);
			
			if (closestOnSegment.HasValue)
			{
				// Bereken de afstand van de ray tot dit punt
				// We projecteren het punt op de ray en berekenen de loodrechte afstand
				Vector3 toPoint = closestOnSegment.Value - from;
				float projectionLength = toPoint.Dot(rayDir);
				Vector3 projectionPoint = from + rayDir * projectionLength;
				float dist = (closestOnSegment.Value - projectionPoint).Length();
				
				if (i < 3) // Debug eerste paar segmenten
				{
					GD.Print($"  Segment {i}: dist={dist:F3}m, closest={closestOnSegment.Value}");
				}
				
				if (dist < closestDistance)
				{
					closestDistance = dist;
					closestPointOnLine = closestOnSegment.Value;
					// Als startIndex is -1, dan is dit een corner->csvpoint segment, gebruik endIndex - 1
					// Anders gebruik startIndex
					insertAfterIndex = segment.startIndex >= 0 ? segment.startIndex : segment.endIndex - 1;
					
					GD.Print($"  → Nieuwe beste: segment {i}, dist={dist:F3}m, insertAfter={insertAfterIndex}");
				}
			}
		}
		
		GD.Print($"[EDIT MODE] Closest distance: {closestDistance:F3}m, threshold: {maxClickDistance:F3}m");
		
		// Als we een punt hebben gevonden binnen de threshold, toon confirmation dialog
		if (closestPointOnLine.HasValue && closestDistance < maxClickDistance && insertAfterIndex >= 0)
		{
			GD.Print($"[EDIT MODE] Punt gevonden op positie {closestPointOnLine.Value} na index {insertAfterIndex}");
			GD.Print($"[EDIT MODE] Toon confirmation dialog...");
			
			// Sla positie en index op voor later gebruik
			_pendingSpawnPosition = closestPointOnLine.Value;
			_pendingInsertIndex = insertAfterIndex;
			
			// Update dialog text met positie informatie
			_spawnConfirmDialog.DialogText = $"Wil je een nieuw punt toevoegen?\n\n" +
			                                  $"Positie: ({closestPointOnLine.Value.X:F2}, {closestPointOnLine.Value.Y:F2}, {closestPointOnLine.Value.Z:F2})\n" +
			                                  $"Na punt index: {insertAfterIndex}";
			
			// Toon confirmation dialog
			_spawnConfirmDialog.PopupCentered();
		}
		else
		{
			if (closestPointOnLine.HasValue && closestDistance >= maxClickDistance)
			{
				GD.Print($"[EDIT MODE] ✗ Punt te ver weg: {closestDistance:F3}m > {maxClickDistance:F3}m");
			}
			else if (closestPointOnLine.HasValue && insertAfterIndex < 0)
			{
				GD.Print($"[EDIT MODE] ✗ Ongeldig insertAfterIndex: {insertAfterIndex}");
			}
			else
			{
				GD.Print($"[EDIT MODE] ✗ Geen punt gevonden op lijn");
			}
		}
	}
	
	private Vector3? ClosestPointOnLineSegmentToRay(Vector3 lineStart, Vector3 lineEnd, Vector3 rayOrigin, Vector3 rayDirection)
	{
		// Normaliseer ray direction
		rayDirection = rayDirection.Normalized();
		
		// Check of segment geldig is
		float lineLength = (lineEnd - lineStart).Length();
		if (lineLength < 0.001f) return null; // Te kort segment
		
		// Vereenvoudigde methode: vind het punt op het lijn segment dat het dichtst bij de ray ligt
		// We gebruiken een plane die loodrecht op de camera kijkrichting staat, door het lijn segment
		
		// Bereken het midden van het segment
		Vector3 segmentMid = (lineStart + lineEnd) / 2f;
		
		// Maak een plane loodrecht op de ray direction, door het segment midden
		Plane plane = new Plane(rayDirection, segmentMid);
		
		// Intersect ray met deze plane
		var intersection = plane.IntersectsRay(rayOrigin, rayDirection);
		
		if (!intersection.HasValue)
		{
			// Als er geen intersectie is, probeer de plane door lineStart
			plane = new Plane(rayDirection, lineStart);
			intersection = plane.IntersectsRay(rayOrigin, rayDirection);
			
			if (!intersection.HasValue) return null;
		}
		
		// Project het intersection punt op het lijn segment
		Vector3 pointOnLine = ProjectPointOnLineSegment(intersection.Value, lineStart, lineEnd);
		
		return pointOnLine;
	}
	
	private Vector3 ProjectPointOnLineSegment(Vector3 point, Vector3 lineStart, Vector3 lineEnd)
	{
		Vector3 lineDir = lineEnd - lineStart;
		float lineLength = lineDir.Length();
		
		if (lineLength < 0.001f) return lineStart;
		
		lineDir /= lineLength; // Normalize
		
		// Project point op de lijn
		float t = (point - lineStart).Dot(lineDir);
		
		// Clamp t to line segment
		t = Mathf.Clamp(t, 0, lineLength);
		
		return lineStart + lineDir * t;
	}
}
