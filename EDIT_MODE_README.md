# 🔧 Edit Mode - Interactief Trap Visualisatie Bewerken

## Overzicht
Met de **Edit Mode** functionaliteit kun je de hoekpunten (corner points) en CSV punten van je trap visualisatie interactief aanpassen. De 3D mesh wordt realtime bijgewerkt tijdens het verslepen van punten.

## Bestanden
- **EditablePoint.cs** - Versleepbare punt node met hover/select states
- **EditModeManager.cs** - Beheert raycasting, drag & drop, en mesh updates
- **EditModeUI.cs** - UI overlay met Edit Mode toggle knop
- **CsvPointPlacer.cs** (aangepast) - Ondersteunt edit mode en realtime rebuilds

## Gebruik

### 1. Edit Mode Activeren
- Klik op de **🔧 Edit Mode (OFF)** knop linksboven
- De knop wordt groen: **🔧 Edit Mode (ON)**
- De muis wordt automatisch zichtbaar
- Alle punten worden weergegeven als interactieve bollen:
  - **Blauwe bollen** = Originele CSV punten
  - **Gele bollen** = Corner points (automatisch gegenereerde hoekpunten)

### 2. Punten Selecteren en Verslepen
- **Hover over een punt** → Punt wordt geel en gloeit
- **Klik op een punt** → Punt wordt groen (geselecteerd)
- **Sleep het punt** → De mesh wordt realtime bijgewerkt
- **Laat los** → Punt blijft op nieuwe positie

### 3. Edit Mode Uitschakelen
- Klik opnieuw op de knop: **🔧 Edit Mode (ON)** → **🔧 Edit Mode (OFF)**
- Alle edit punten worden verborgen
- De muis gaat terug naar camera control mode

## Sneltoetsen
- **Linker muisknop** - Selecteer/versleep punt
- **ESC** - Deselecteer huidig punt (tijdens edit mode)
- **ESC** - Toggle muis capture (buiten edit mode)

## Technische Details

### Raycasting Systeem
De EditModeManager gebruikt Godot's `PhysicsRayQueryParameters3D` om:
1. Van camera naar muispositie te casten
2. Collisions met EditablePoint nodes te detecteren
3. Hover en select states bij te werken

### Drag & Drop Implementatie
```csharp
// 1. Maak een drag plane parallel aan camera view
var cameraNormal = -_camera.GlobalTransform.Basis.Z;
_dragPlane = new Plane(cameraNormal, _selectedPoint.GlobalPosition);

// 2. Intersect ray met plane tijdens drag
var planeIntersect = _dragPlane.IntersectsRay(from, rayDir);

// 3. Update punt positie + rebuild mesh
_selectedPoint.Position = planeIntersect.Value + _dragOffset;
_pointPlacer.RebuildFromCurrentData();
```

### Realtime Mesh Updates
Tijdens het verslepen:
1. **UpdatePoint()** of **UpdateCornerPoint()** past de interne lijst aan
2. **RebuildFromCurrentData()** rebuildt alle geometrie:
   - Punt visualisaties (rode bollen)
   - Level lines (groene verbindingslijnen)
   - Corner points (witte bollen)
   - Stair steps (grijze gevulde treden)

### Visuele Feedback
```
NORMAL:   Blauw/Geel, geen emissie
HOVER:    Geel met glow (emission 0.5)
SELECTED: Groen met sterke glow (emission 0.8)
```

## Beperkingen & Toekomstige Features
- [x] Realtime mesh update tijdens drag
- [x] Hover/select visual feedback
- [x] Zowel CSV als corner points editable
- [ ] Undo/Redo functionaliteit
- [ ] Opslaan van gewijzigde punten naar CSV
- [ ] Multi-select en groep transformaties
- [ ] Snap-to-grid functionaliteit
- [ ] Hoogte-locking (alleen XZ verplaatsing)

## Debug
Bekijk de Godot console voor:
```
[EDIT MODE] ENABLED
[EDIT MODE] Creating 24 CSV points + 12 corner points
```

## Integratie
Om edit mode toe te voegen aan je bestaande scene:
```gdscript
# In node_3d.tscn
[node name="CanvasLayer" type="CanvasLayer" parent="."]

[node name="EditModeUI" type="Control" parent="CanvasLayer"]
script = ExtResource("EditModeUI.cs")
```

De EditModeManager wordt automatisch toegevoegd door CsvPointPlacer._Ready().
