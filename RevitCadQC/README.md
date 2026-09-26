# Revit CAD Technical QC

A Revit add-in that checks a Revit model against the CAD plans it was built from, element by element, and marks every difference in Revit, in CAD, and in a report.

Examples of what it catches:

* The 2nd floor CAD says the wall is 230 mm (drawn 230 and noted "230 THK") but it was modelled with a 200 mm type.
* A wall in the CAD plan was never modelled, or was modelled partly, or in the wrong place.
* A wall, door, window or column exists in Revit but not in the CAD.
* A door is 900 in CAD and 1000 in Revit, a window is missing, a door was modelled as a window.
* A 300×450 column is shifted 60 mm, is the wrong size, or is turned 90°.
* A grid is off by 8 mm.
* A CAD dimension reads 3000 but the same two wall faces in Revit are 2950 apart.
* "TOILET" and "BATH" are separate rooms in CAD but one room in Revit, so a wall is missing.
* Drafting errors inside the CAD itself: overridden dimension text, thickness notes that do not match the drawn wall.

You point it at a folder. It finds every DWG/DXF, reads DWG directly, works out which floor each plan is, aligns each plan to the model, and runs the checks. You do not export DXF by hand, and you do not need to set up the floor mapping.

# What you get after a run

| Where | What |
|---|---|
| Revit | One floor plan per checked floor named `QC - 2nd Floor - <date>`. The CAD is linked in at the right place (halftone), and every issue has a cloud, an ID label and, for missing walls, a line where the wall should be. Elements with issues are shown red (critical), orange (major) or yellow (minor). There is also a `QC 3D` view. |
| Revit | Issue Browser (modeless). Double-click an issue to open its QC plan, zoom to it and select the elements. Mark false positives as Accepted so they stay closed on the next run. |
| CAD | `CAD_Markup\<file>_QC_markup.scr`: open the original DWG in AutoCAD, type `SCRIPT`, pick this file. Clouds, labels and the missing or extra geometry are drawn on layers `QC_CRITICAL`, `QC_MAJOR`, `QC_MINOR`, `QC_INFO`, and the CAD entities involved are pre-selected. |
| CAD | `CAD_Markup\<file>_QC_markup.dxf` and `.dwg`: the same markup plus the Revit walls, columns and openings drawn over the CAD (layer `QC_REVIT_*`). XREF or INSERT it at 0,0. |
| Report | `QC_Report.html`: summary, a zoomable plan per floor with the CAD, the Revit model and the issues on top, and a filterable issue register. Works offline and can be emailed. |
| Report | `QC_Issues.xlsx` and `QC_Issues.csv`: the issue register for Excel. |
| Report | `qc_report.json` plus `history\`: used to mark issues New, Open, Accepted or Resolved between runs. |

A sample of every output, made from the test drawing, is in [`docs/sample-output`](docs/sample-output).

# Install

Plain-text guide with every step: [`README.txt`](README.txt).

1. Nothing to install for DWG: files are read directly with ACadSharp 3.6.35 (pinned to the same version as AashirTools so both add-ins can run together). The free ODA File Converter or AutoCAD's `accoreconsole.exe` are only used as a fallback if a DWG cannot be read.
2. Close Revit and double-click **`RUN_1_BUILD_AND_INSTALL.bat`**. It installs a user-local .NET 8 SDK if needed (no admin), runs the tests, builds for every Revit version on the PC, signs the DLLs with a self-signed certificate, installs, and verifies the copy by hash and signature.
3. Start Revit. The **CAD QC** tab appears.

| File | What it does |
|---|---|
| `RUN_1_BUILD_AND_INSTALL.bat` | Build, sign, install, verify. Run after every change. Refuses to run while Revit is open. |
| `RUN_BUILD_ONLY_R2025.bat`, `RUN_BUILD_ONLY_R2022.bat` | Build only into `src\CadQC.Revit\bin\R20xx\`, Revit may stay open (Add-in Manager loop). |
| `RUN_2_TEAMMATE_TRUST_CERT.bat` | Trust the signing certificate on another PC. |
| `RUN_3_UNINSTALL.bat` | Remove from every Revit version. |
| `RUN_4_MAKE_TEAM_PACKAGE.bat` | Create `RevitCadQC_TEAM_PACKAGE` (+ zip) on the Desktop. Teammates double-click `INSTALL_FOR_TEAMMATE.BAT`. |

Supported: Revit 2022, 2023, 2024 (.NET Framework 4.8), 2025 and 2026 (.NET 8). Manual build: `dotnet build src\CadQC.Revit\CadQC.Revit.csproj -c R2025`.

# Use

1. Open the Revit project. On the **CAD QC** tab, click **Run CAD QC**.
2. Pick the folder with the CAD plans. Subfolders are included.
3. Choose the checks and tolerances, or keep the defaults. Leave the floor mapping empty to match floors automatically.
4. Click **Run QC**. When it finishes you can open the Issue Browser, the HTML report or the output folder. The first QC plan opens.
5. Fix the model and run again. Fixed issues show as Resolved, and new ones as New.

**Clear QC Views** removes every QC view together with its clouds and linked CAD. **Settings** opens the project's settings file. **Export Snapshot** saves the model data so QC can run from the command line.

# Which CAD layer is what

1. **Consultant profile.** The first time a consultant's drawings are seen, a review grid lists every layer with the tool's guess. Correct it once and save; it is stored in `%APPDATA%\RevitCadQC\CadProfiles` and used automatically for every drawing that shares at least 60% of its layers. The team package carries the profiles.
2. **`CadLayerDictionary.txt`.** Notepad-editable token rules, first match wins (see [`config/CadLayerDictionary.txt`](config/CadLayerDictionary.txt)). Your copy lives in `%APPDATA%\RevitCadQC`.
3. Unknown layers are skipped and listed in the run log, never guessed. If no layer is classed as wall, wall layers are detected from the linework.

# How floors are matched

Floors are matched from the file name, from the plan title inside the drawing, and from the Revit level names:

* `2ND FLOOR PLAN.dwg`, `SECOND FLOOR.dwg`, `L02.dwg`, `Level 2.dxf` → floor 2
* `GF`, `GROUND FLOOR`, `STILT` → 0 · `B1`, `BASEMENT` → -1 · `TERRACE`, `ROOF` → roof
* `TYPICAL FLOOR PLAN (3RD TO 7TH)` → checked against every level from 3 to 7
* One DWG sheet with several plans (titles such as "GROUND FLOOR PLAN", "FIRST FLOOR PLAN" under each plan) is split, and each plan is checked against its own level.
* Revit levels named `Level 1, Level 2…` with Level 1 at ±0 follow the Revit template convention (Level 1 = ground).

If a file cannot be matched, the run log says so. Add a row in the floor mapping (CAD file, optional plan title, Revit level) to fix it.

# How the CAD is lined up with the model

No manual alignment is needed. For each floor the tool tries, in order:

1. **Identity**: the CAD is already in model coordinates (origin to origin).
2. **Grids**: grids with the same names in CAD and Revit (for example "A" and "1"). The intersections give an exact transform.
3. **Best fit**: the extents are matched in four orientations, then refined by ICP (iterative closest point) on the wall centre lines. This handles CAD drawn anywhere, at any rotation.

The winner is the transform that puts the most CAD walls on Revit walls. The report shows the method, the match percentage and the RMS error. If the fit is poor, a critical **Alignment uncertain** issue is raised so wrong results are never silently reported. You can then enter an offset and rotation in the floor mapping (`ManualOffsetX`, `ManualOffsetY`, `ManualRotationDeg`).

# The checks in detail

**Walls.** CAD walls are rebuilt from the double lines on wall layers: parallel lines are paired (closest spacing first, so corridors, cavity walls and shafts are not confused), and junctions are merged while openings are kept. This gives each wall's centre line and real thickness. Thickness notes such as `230 THK`, `230MM THK BRICK WALL`, `115 WALL` and `9" WALL` are linked to the nearest wall.

* Missing in Revit (whole or partial, with the missing length)
* Extra in Revit (door and window gaps and wall junctions are not counted)
* Thickness mismatch: CAD 230 against Revit 200. The Revit total width and core width are both checked (`RevitWallWidthMode`: Auto, Total or Core), and accepted pairs can be listed, for example CAD 230 = Revit 250 with plaster (`ThicknessEquivalents`).
* Position offset, reported with both face offsets. An offset that only comes from a thickness difference with one face aligned is folded into the thickness issue.
* CAD wall modelled as curtain wall
* Revit: type name says 230 but the type is 200 wide; overlapping or duplicate walls

**Doors and windows.** CAD openings come from door and window blocks (width from the swing arc, a WIDTH attribute or the block extents), loose swing arcs, and window linework. Tags such as D1, W2 and V1 are read. The checks: missing, extra, width, position along the wall, door against window, and CAD tag against Revit Type Mark or Mark.

**Columns.** CAD columns come from closed outlines, hatches, loose line loops, circles and column blocks. Revit columns are measured from their real geometry. The checks: missing, extra, size, position and rotation (a 230×450 turned 90°).

**Grids.** Grids are matched by bubble name. The checks: missing, extra, angle and offset.

**Rooms.** CAD room names come from text on room layers or text containing a room word (BEDROOM, TOILET, PUJA…), with size notes such as `3000 X 3600` or `10'0" X 12'0"` and area notes. The checks: no Revit room at the label, name mismatch, clear size and area, two CAD rooms inside one Revit room (a wall is missing), and unplaced or unenclosed Revit rooms.

**Dimensions.** Every CAD linear dimension that runs between wall faces or grids is measured again between the same references in Revit. Dimension text that was typed over is flagged.

**CAD drafting.** Thickness notes that contradict the drawn wall, overridden dimensions, and single lines on wall layers.

# Settings

Settings are saved next to the model as `<model>_CadQC.json`, so each project keeps its own settings. Open them from **Settings** or from **Advanced settings** in the run dialog. A full default file is in [`config/qc-settings.sample.json`](config/qc-settings.sample.json). The most used options:

| Setting | Default | Meaning |
|---|---|---|
| `WallLayers`, `DoorLayers`, `WindowLayers`, `ColumnLayers`, `GridLayers`, `RoomLayers` | common names such as `WALL\|A-WALL\|BRICK…` | Regular expressions for the layer names. If no layer matches, wall layers are detected automatically from how the lines behave. |
| `ExcludeLayers` | `FURN\|HATCH\|TITLE…` | Layers that are never read |
| `CadUnits` | `auto` | Detected from the header and checked against the wall spacings. You can force `mm`, `cm`, `m`, `in` or `ft`. |
| `WallThicknessTol` | 5 mm | Allowed thickness difference |
| `WallPositionTolMinor` / `Major` | 10 / 50 mm | Offsets reported as minor or major |
| `MinIssueLength` | 300 mm | Shorter missing or extra wall pieces are ignored |
| `OpeningWidthTol`, `OpeningPositionTol` | 20 / 50 mm | Door and window tolerances |
| `ColumnSizeTol`, `ColumnPositionTol` | 10 / 15 mm | Column tolerances |
| `GridOffsetTol` | 5 mm | Grid tolerance |
| `DimensionTol` | 10 mm | CAD dimension against Revit |
| `RevitWallWidthMode` | Auto | Compare the CAD thickness with the Revit total width, the core width, or either |
| `ThicknessEquivalents` | none | For example `[{"Cad":230,"Revit":[250]}]` |
| `CutPlaneHeight` | 1200 mm | Elements cut at this height above a level belong to that floor |
| `IncludeLinkedModels` | false | Also read walls, doors and other elements from linked RVTs |
| `FloorMappings` | empty | Manual floor mapping and manual alignment |

# Command line (no Revit needed)

In Revit use **Export Snapshot** once, then:

```
CadQC --snapshot model_snapshot.json --cad "D:\Project\CAD" --out "D:\Project\QC" [--settings project_CadQC.json]
```

This writes the same reports and CAD markups. The exit code is 1 when there are critical issues, which is useful for a nightly check when new CAD issues arrive.

# Project layout

```
RevitCadQC/
  src/CadQC.Core/        engine, no Revit dependency (netstandard2.0)
    Dxf/                 DXF reader (blocks, OCS, hatches, dimensions, MTEXT…)
    Conversion/          DWG reading with ACadSharp (ODA / AutoCAD fallback), cached
    Extraction/          walls, openings, columns, grids, rooms, dimensions, units, plan splitting, text parsing
    Alignment/           CAD → Revit auto alignment (identity, grids, ICP)
    Compare/             the checks
    Report/              HTML, Excel, CSV, JSON, CAD markup DXF + AutoCAD script, run-to-run tracking
    Engine/              orchestration, floor/level matching
  src/CadQC.Revit/       the add-in: ribbon, model extraction, QC views and overrides, issue browser
  src/CadQC.Cli/         command-line runner
  tests/CadQC.Tests/     tests on a synthetic 2nd floor plan with planted errors
  config/                default settings
  RUN_*.bat, *.ps1       build, sign, install, team package
  docs/sample-output/    every output produced from the test drawing
```

# Limits

* The checks are 2D, on plan: what is cut at the plan cut height. Heights, sill levels and sections are not compared with CAD elevations.
* Xrefs that are not bound are reported and skipped. Bind them, or put the xref DWGs in the CAD folder.
* Dynamic blocks are recognised by layer, swing arcs and linework, because their anonymous names lose the original block name.
* Curved walls are compared chord by chord (about 500 mm chords).
* Single-line walls, where the thickness is not drawn, are reported as drafting items rather than compared for thickness.
