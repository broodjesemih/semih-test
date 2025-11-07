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

	public bool IsEditMode { get; private set; } = false;

	public override void _Ready()
	{
		_pointPlacer = GetParent<CsvPointPlacer>();
		_camera = GetViewport().GetCamera3D();
		
		_editPointsParent = new Node3D { Name = "EditPoints" };
		AddChild(_editPointsParent);
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
	}

	private void ExitEditMode()
	{
		_isDragging = false;
		_selectedPoint = null;
		_hoveredPoint = null;
		ClearEditPoints();
	}

	private void ClearEditPoints()
	{
		foreach (var point in _editablePoints)
		{
			point.QueueFree();
		}
		_editablePoints.Clear();
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

		// Linker muisklik: selecteer/start drag
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
}
