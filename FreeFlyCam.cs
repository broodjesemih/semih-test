using Godot;

public partial class FreeFlyCam : Camera3D   // <- als je script op een Node3D zit, verander dit naar : Node3D
{
	[Export] public float MovementSpeed = 4f;
	[Export] public float SprintMultiplier = 2f;
	[Export] public float MouseSensitivity = 0.5f;

	private float _yaw;
	private float _pitch;

	public override void _Ready()
	{
		EnsureInputActions();
		Input.MouseMode = Input.MouseModeEnum.Captured;

		var rot = RotationDegrees;
		_yaw = rot.Y;
		_pitch = rot.X;
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
		{
			_yaw   -= mm.Relative.X * MouseSensitivity * 0.1f;
			_pitch -= mm.Relative.Y * MouseSensitivity * 0.1f;
			_pitch = Mathf.Clamp(_pitch, -89f, 89f);
			RotationDegrees = new Vector3(_pitch, _yaw, 0);
		}

		if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
		{
			Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
				? Input.MouseModeEnum.Visible
				: Input.MouseModeEnum.Captured;
		}
	}

	public override void _Process(double delta)
	{
		var dir = Vector3.Zero;
		if (Input.IsActionPressed("move_forward")) dir -= Transform.Basis.Z;
		if (Input.IsActionPressed("move_back"))    dir += Transform.Basis.Z;
		if (Input.IsActionPressed("move_left"))    dir -= Transform.Basis.X;
		if (Input.IsActionPressed("move_right"))   dir += Transform.Basis.X;
		if (Input.IsActionPressed("move_up"))      dir += Transform.Basis.Y;
		if (Input.IsActionPressed("move_down"))    dir -= Transform.Basis.Y;

		if (dir != Vector3.Zero) dir = dir.Normalized();

		float speed = MovementSpeed * (Input.IsActionPressed("sprint") ? SprintMultiplier : 1f);
		GlobalTranslate(dir * speed * (float)delta);
	}

	// ---- InputMap helpers ----
	private void EnsureInputActions()
	{
		Ensure("move_forward", Key.W, Key.Up);
		Ensure("move_back",    Key.S, Key.Down);
		Ensure("move_left",    Key.A, Key.Left);
		Ensure("move_right",   Key.D, Key.Right);
		Ensure("move_up",      Key.Space);
		Ensure("move_down",    Key.Ctrl);
		Ensure("sprint",       Key.Shift);
	}

	private void Ensure(string action, Key primary, Key alt = Key.None)
	{
		if (!InputMap.HasAction(action))
			InputMap.AddAction(action);

		bool HasKey(Key k)
		{
			foreach (var e in InputMap.ActionGetEvents(action))
				if (e is InputEventKey kev && kev.Keycode == k) return true;
			return false;
		}

		void AddKey(Key k)
		{
			if (k == Key.None || HasKey(k)) return;
			InputMap.ActionAddEvent(action, new InputEventKey { Keycode = k });
		}

		AddKey(primary);
		AddKey(alt);
	}
}
