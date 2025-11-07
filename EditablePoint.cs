using Godot;

/// <summary>
/// Vertegenwoordigt een versleepbaar punt in edit mode
/// </summary>
public partial class EditablePoint : StaticBody3D
{
	public Vector3 OriginalPosition { get; set; }
	public int PointIndex { get; set; }
	public bool IsCornerPoint { get; set; }
	
	private MeshInstance3D _visual;
	private CollisionShape3D _collision;
	private StandardMaterial3D _normalMaterial;
	private StandardMaterial3D _hoverMaterial;
	private StandardMaterial3D _selectedMaterial;
	private bool _isHovered = false;
	private bool _isSelected = false;

	public override void _Ready()
	{
		// Collision shape voor mouse picking
		_collision = new CollisionShape3D();
		var sphere = new SphereShape3D { Radius = 0.035f };
		_collision.Shape = sphere;
		AddChild(_collision);

		// Visual mesh
		_visual = new MeshInstance3D();
		var mesh = new SphereMesh { Radius = 0.015f, Height = 0.035f };
		_visual.Mesh = mesh;
		AddChild(_visual);

		// Materials
		_normalMaterial = new StandardMaterial3D 
		{ 
			AlbedoColor = IsCornerPoint ? new Color(1, 1, 0) : new Color(0, 0.5f, 1),
			EmissionEnabled = false
		};
		_hoverMaterial = new StandardMaterial3D 
		{ 
			AlbedoColor = new Color(1, 1, 0),
			EmissionEnabled = true,
			Emission = new Color(1, 1, 0)
		};
		_selectedMaterial = new StandardMaterial3D 
		{ 
			AlbedoColor = new Color(0, 1, 0),
			EmissionEnabled = true,
			Emission = new Color(0, 1, 0)
		};

		_visual.MaterialOverride = _normalMaterial;
	}

	public void SetHovered(bool hovered)
	{
		_isHovered = hovered;
		UpdateVisual();
	}

	public void SetSelected(bool selected)
	{
		_isSelected = selected;
		UpdateVisual();
	}

	private void UpdateVisual()
	{
		if (_visual == null) return;
		
		if (_isSelected)
			_visual.MaterialOverride = _selectedMaterial;
		else if (_isHovered)
			_visual.MaterialOverride = _hoverMaterial;
		else
			_visual.MaterialOverride = _normalMaterial;
	}
}
