using Godot;

public partial class EditModeUI : Control
{
private Button _editButton;
private Button _debugButton;
private Button _undoButton;
private Button _redoButton;
private Button _autoCorrectButton;
private Button _loadCsvButton;
private Label _statusLabel;
private CsvPointPlacer _pointPlacer;
private FileDialog _fileDialog;

public override void _Ready()
{
// Haal knoppen en labels uit de scene (moeten in Godot editor aangemaakt zijn)
_editButton = GetNodeOrNull<Button>("VBoxContainer/EditButton");
_debugButton = GetNodeOrNull<Button>("VBoxContainer/DebugButton");
_undoButton = GetNodeOrNull<Button>("VBoxContainer/UndoButton");
_redoButton = GetNodeOrNull<Button>("VBoxContainer/RedoButton");
_autoCorrectButton = GetNodeOrNull<Button>("VBoxContainer/AutoCorrectButton");
_loadCsvButton = GetNodeOrNull<Button>("VBoxContainer/LoadCsvButton");
_statusLabel = GetNodeOrNull<Label>("VBoxContainer/StatusLabel");

// Fallback: als nodes niet bestaan, maak ze in code (backwards compatibility)
if (_editButton == null || _debugButton == null || _undoButton == null || _redoButton == null || _autoCorrectButton == null || _loadCsvButton == null || _statusLabel == null)
{
GD.PrintErr("UI nodes niet gevonden in scene, maak ze dynamisch aan");
CreateUIInCode();
}
else
{
// Verbind signals van bestaande knoppen
_editButton.Pressed += OnEditButtonPressed;
_debugButton.Pressed += OnDebugButtonPressed;
_undoButton.Pressed += OnUndoButtonPressed;
_redoButton.Pressed += OnRedoButtonPressed;
_autoCorrectButton.Pressed += OnAutoCorrectButtonPressed;
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

_debugButton = new Button();
_debugButton.Name = "DebugButton";
_debugButton.Text = "Debug Mode (OFF)";
_debugButton.CustomMinimumSize = new Vector2(200, 50);
_debugButton.Pressed += OnDebugButtonPressed;
vbox.AddChild(_debugButton);

// Undo/Redo knoppen in een HBoxContainer
var undoRedoBox = new HBoxContainer();
undoRedoBox.Name = "UndoRedoBox";
vbox.AddChild(undoRedoBox);

_undoButton = new Button();
_undoButton.Name = "UndoButton";
_undoButton.Text = "Undo";
_undoButton.CustomMinimumSize = new Vector2(95, 50);
_undoButton.Pressed += OnUndoButtonPressed;
undoRedoBox.AddChild(_undoButton);

_redoButton = new Button();
_redoButton.Name = "RedoButton";
_redoButton.Text = "Redo";
_redoButton.CustomMinimumSize = new Vector2(95, 50);
_redoButton.Pressed += OnRedoButtonPressed;
undoRedoBox.AddChild(_redoButton);

_autoCorrectButton = new Button();
_autoCorrectButton.Name = "AutoCorrectButton";
_autoCorrectButton.Text = "Smooth Stairpoints";
_autoCorrectButton.CustomMinimumSize = new Vector2(200, 50);
_autoCorrectButton.Pressed += OnAutoCorrectButtonPressed;
vbox.AddChild(_autoCorrectButton);

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

private void OnDebugButtonPressed()
{
GD.Print("[UI] Debug button pressed!");
if (_pointPlacer != null)
{
_pointPlacer.ToggleDebugMode();
}
else
{
GD.PrintErr("[UI] PointPlacer is NULL!");
}
}

private void OnUndoButtonPressed()
{
GD.Print("[UI] Undo button pressed!");
if (_pointPlacer != null)
{
_pointPlacer.Undo();
_statusLabel.Text = "Undo uitgevoerd";
}
else
{
GD.PrintErr("[UI] PointPlacer is NULL!");
}
}

private void OnRedoButtonPressed()
{
GD.Print("[UI] Redo button pressed!");
if (_pointPlacer != null)
{
_pointPlacer.Redo();
_statusLabel.Text = "Redo uitgevoerd";
}
else
{
GD.PrintErr("[UI] PointPlacer is NULL!");
}
}

private void OnAutoCorrectButtonPressed()
{
GD.Print("[UI] Smooth button pressed!");
if (_pointPlacer != null)
{
_pointPlacer.AutoCorrectCSV();
_statusLabel.Text = "CSV Smoothed";
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

bool isDebugMode = _pointPlacer.IsDebugMode();
_debugButton.Text = isDebugMode ? "Debug Mode (ON)" : "Debug Mode (OFF)";
_debugButton.Modulate = isDebugMode ? new Color(0.5f, 1f, 0.5f) : new Color(1, 1, 1);

// Update undo/redo button states
_undoButton.Disabled = !_pointPlacer.CanUndo();
_undoButton.Modulate = _pointPlacer.CanUndo() ? new Color(1, 1, 1) : new Color(0.5f, 0.5f, 0.5f);

_redoButton.Disabled = !_pointPlacer.CanRedo();
_redoButton.Modulate = _pointPlacer.CanRedo() ? new Color(1, 1, 1) : new Color(0.5f, 0.5f, 0.5f);
}
}
}
