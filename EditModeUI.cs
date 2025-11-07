using Godot;

public partial class EditModeUI : Control
{
private Button _editButton;
private Button _loadCsvButton;
private Label _statusLabel;
private CsvPointPlacer _pointPlacer;
private FileDialog _fileDialog;

public override void _Ready()
{
// Haal knoppen en labels uit de scene (moeten in Godot editor aangemaakt zijn)
_editButton = GetNodeOrNull<Button>("VBoxContainer/EditButton");
_loadCsvButton = GetNodeOrNull<Button>("VBoxContainer/LoadCsvButton");
_statusLabel = GetNodeOrNull<Label>("VBoxContainer/StatusLabel");

// Fallback: als nodes niet bestaan, maak ze in code (backwards compatibility)
if (_editButton == null || _loadCsvButton == null || _statusLabel == null)
{
GD.PrintErr("UI nodes niet gevonden in scene, maak ze dynamisch aan");
CreateUIInCode();
}
else
{
// Verbind signals van bestaande knoppen
_editButton.Pressed += OnEditButtonPressed;
_loadCsvButton.Pressed += OnLoadCsvButtonPressed;
}

// Vind de MultiMeshInstance3D in de scene tree
// EditModeUI is onder CanvasLayer die onder Node3D zit
_pointPlacer = GetNode<CsvPointPlacer>("../../MultiMeshInstance3D");
if (_pointPlacer == null)
{
GD.PrintErr("CsvPointPlacer niet gevonden! Check de scene structuur.");
}

// FileDialog aanmaken (kan niet visueel in scene vanwege popup)
_fileDialog = new FileDialog();
_fileDialog.FileMode = FileDialog.FileModeEnum.OpenFile;
_fileDialog.Access = FileDialog.AccessEnum.Filesystem;
_fileDialog.AddFilter("*.csv", "CSV Files");
_fileDialog.Size = new Vector2I(800, 600);
_fileDialog.FileSelected += OnFileSelected;
AddChild(_fileDialog);
}

private void CreateUIInCode()
{
var vbox = new VBoxContainer();
vbox.Name = "VBoxContainer";
vbox.Position = new Vector2(10, 10);
AddChild(vbox);

_editButton = new Button();
_editButton.Name = "EditButton";
_editButton.Text = "Edit Mode (OFF)";
_editButton.CustomMinimumSize = new Vector2(200, 50);
_editButton.Pressed += OnEditButtonPressed;
vbox.AddChild(_editButton);

_loadCsvButton = new Button();
_loadCsvButton.Name = "LoadCsvButton";
_loadCsvButton.Text = "Load CSV File";
_loadCsvButton.CustomMinimumSize = new Vector2(200, 50);
_loadCsvButton.Pressed += OnLoadCsvButtonPressed;
vbox.AddChild(_loadCsvButton);

_statusLabel = new Label();
_statusLabel.Name = "StatusLabel";
_statusLabel.Text = "";
_statusLabel.AddThemeColorOverride("font_color", new Color(1, 1, 1));
vbox.AddChild(_statusLabel);
}

private void OnEditButtonPressed()
{
GD.Print("[UI] Edit button pressed!");
if (_pointPlacer != null)
{
GD.Print("[UI] PointPlacer found, toggling edit mode");
_pointPlacer.ToggleEditMode();
}
else
{
GD.PrintErr("[UI] PointPlacer is NULL!");
}
}

private void OnLoadCsvButtonPressed()
{
// Open de file dialog
_fileDialog.PopupCentered();
}

private void OnFileSelected(string path)
{
// Laad het geselecteerde CSV bestand
if (_pointPlacer != null)
{
_pointPlacer.LoadCsvFile(path);
_statusLabel.Text = $"CSV geladen: {path}";
}
}

public override void _Process(double delta)
{
if (_pointPlacer != null)
{
bool isEditMode = _pointPlacer.IsEditMode();
_editButton.Text = isEditMode ? "Edit Mode (ON)" : "Edit Mode (OFF)";
_editButton.Modulate = isEditMode ? new Color(0.5f, 1f, 0.5f) : new Color(1, 1, 1);
}
}
}
