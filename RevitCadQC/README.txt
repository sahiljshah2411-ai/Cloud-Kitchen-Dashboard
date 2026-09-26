REVIT CAD QC (CAD vs 3D TECHNICAL QC) - Revit 2022 / 2023 / 2024 / 2025 / 2026
=====================================================================

WHAT IT IS
  Checks the Revit model against the CAD plans it was built from,
  element by element, and marks every difference in Revit, in CAD and
  in a report. Point it at a folder of DWG/DXF plans. DWG is converted
  to DXF automatically - nothing is exported by hand.

TAB: "CAD QC"
  panel "Technical QC"
  Run CAD QC       pick the CAD folder -> (layer review, only for a new
                   consultant) -> checks every floor -> QC views, Issue
                   Browser, HTML + Excel report, CAD markups
  Issue Browser    list of issues; double-click = open QC plan, zoom,
                   select elements. "Accept" = not an error, stays
                   closed on the next run
  Layer Mapping    review / correct which CAD layers are walls, doors,
                   windows, columns, grids, rooms. Saved per consultant
  Clear QC Views   delete every "QC - ..." view with its clouds and
                   linked CAD, clear QC comments
  panel "Setup"
  Settings         this project's settings file (<model>_CadQC.json)
  Layer Dictionary open CadLayerDictionary.txt in Notepad
  Export Snapshot  model data to JSON for the command-line runner
  Version          version, install path, dictionary and profiles in use

WHAT IT CHECKS (per floor)
  WALLS    missing / partly missing / extra in Revit / thickness
           (CAD 230 drawn or "230 THK" note vs Revit 200) / position
           (both faces reported) / modelled as curtain wall.
           Revit total OR core width accepted (RevitWallWidthMode),
           accepted pairs e.g. CAD 230 = Revit 250 (ThicknessEquivalents)
  DOORS / WINDOWS
           missing / extra / width / position / door vs window /
           CAD tag D1, W2 vs Revit Type Mark or Mark
  COLUMNS  missing / extra / size / position / turned 90 degrees
  GRIDS    missing / extra / offset / angle (matched by bubble name)
  ROOMS    no Revit room at the CAD label / name / clear size
           ("3000 X 3600", 10'0" X 12'0") / area / two CAD rooms inside
           one Revit room (= wall missing) / unplaced or unenclosed rooms
  DIMENSIONS
           every CAD linear dimension between wall faces or grids is
           re-measured between the same references in Revit
  CAD DRAFTING
           overridden dimension text, thickness note vs drawn wall,
           single lines on wall layers
  REVIT MODEL
           type name says 230 but type is 200 wide, overlapping walls

HOW FLOORS ARE MATCHED (no setup)
  File names and plan titles: "2ND FLOOR PLAN", "SECOND FLOOR", "L02",
  "GF", "STILT", "B1", "TERRACE", "TYPICAL FLOOR PLAN (3RD TO 7TH)".
  One DWG sheet with several plans is split by the plan titles.
  Revit "Level 1, Level 2..." with Level 1 at +-0 = ground floor.
  Anything it cannot match is listed in the run log - add a row in the
  floor mapping (CAD file, optional plan title, Revit level).

CAD LAYERS - WHICH LAYER IS WHAT (no guessing)
  1. Consultant profile  - exact layer names, confirmed ONCE in the
     review grid, then remembered. Stored in
       %APPDATA%\RevitCadQC\CadProfiles
     (outside the tool folder - reinstalling never deletes them; the team
     package carries them). A drawing uses the profile that knows >= 60%
     of its layers. Tick "Review the CAD layer mapping" in Run CAD QC to
     look again and overwrite.
  2. CadLayerDictionary.txt - token rules, Notepad-editable, FIRST MATCH
     WINS (IGNORE before WALL so "A-WALL-PATT" is not a wall). Your copy:
       %APPDATA%\RevitCadQC\CadLayerDictionary.txt
     Shipped copy next to CadQC.Revit.dll. Missing/bad file = built-in.
  3. Unknown layers are skipped and listed in the run log - never guessed.
  If nothing is classed as wall, wall layers are detected from how the
  lines behave (parallel pairs at wall spacing) and reported.

HOW CAD IS LINED UP WITH THE MODEL (no setup)
  1. identity (CAD already in model coordinates)
  2. grid names (A, B, 1, 2... intersections) - exact
  3. best fit: extents x 4 rotations + ICP on wall centre lines
  The one that puts most CAD walls on Revit walls wins. Poor fit ->
  CRITICAL "Alignment uncertain" issue, never silent. Manual offset/
  rotation per floor: ManualOffsetX / ManualOffsetY / ManualRotationDeg.

OUTPUT
  Revit   "QC - <floor> - <date>" plan per floor: CAD linked (halftone)
          at the aligned position, cloud + ID label per issue, line
          where a missing wall should be, elements red (critical) /
          orange (major) / yellow (minor). "QC 3D - <date>" view.
          Line styles "QC Critical / Major / Minor / Info".
  CAD     <CAD folder>\_RevitCadQC\CAD_Markup\
            <file>_QC_markup.scr  open the ORIGINAL DWG, type SCRIPT,
                                  pick it: clouds, labels, missing /
                                  extra geometry on layers QC_CRITICAL
                                  QC_MAJOR QC_MINOR QC_INFO, and the CAD
                                  entities involved are pre-selected
            <file>_QC_markup.dxf  (+ .dwg with ODA) same markup plus the
                                  Revit walls/columns/openings on layers
                                  QC_REVIT_*; XREF or INSERT at 0,0
  Report  QC_Report.html   summary + zoomable plan per floor + filters
          QC_Issues.xlsx   issue register for Excel
          QC_Issues.csv
          qc_report.json + history\  - issues marked NEW / OPEN /
                                       ACCEPTED / RESOLVED run to run

SETTINGS
  Per project: <model folder>\<model>_CadQC.json (Settings button).
  Office default: %APPDATA%\RevitCadQC\default_settings.json - every
  new project starts from it; the team package ships it.
  Layer names are regular expressions (WallLayers, DoorLayers,
  WindowLayers, ColumnLayers, GridLayers, RoomLayers, ExcludeLayers).
  If no layer matches, wall layers are detected from the linework.
  Units: detected from the header AND checked against wall spacing
  (a mm header on a drawing in metres or inches is caught).
  Full list with defaults: config\qc-settings.sample.json

BUILD + INSTALL (YOU)
  RUN_1_BUILD_AND_INSTALL.bat - ONE script: dotnet check (installs a
    user-local .NET 8 SDK if missing, no admin), unit tests, build every
    Revit version installed on this PC, certificate, sign, install,
    verify by hash + signature, DWG converter check.
    Run after every change. (Refuses to run while Revit is open.)
    Only some versions:  RUN_1_BUILD_AND_INSTALL.bat -Versions 2022,2025
  RUN_BUILD_ONLY_R2025.bat / RUN_BUILD_ONLY_R2022.bat - build only,
    Revit may stay open, for the Add-in Manager (Faceless) loop.
  RUN_3_UNINSTALL.bat - remove from every Revit version.

DWG READING - NOTHING TO INSTALL
  DWG is read directly by ACadSharp 3.6.35 (built in, same pinned version
  as AashirTools, so both add-ins load side by side without a clash).
  Fallbacks for unusual files, used only if the built-in reader fails:
  ODA File Converter (free) or AutoCAD's accoreconsole.exe, found
  automatically. Readings are cached in _RevitCadQC\.dxf-cache and only
  redone when the DWG changes.

GIVE TO TEAM
  Run RUN_4_MAKE_TEAM_PACKAGE.bat -> creates RevitCadQC_TEAM_PACKAGE
  (+ .zip) on Desktop with the signed R20xx DLLs, .addin, certificate,
  office default settings, your CadLayerDictionary.txt, every confirmed
  consultant profile, INSTALL_FOR_TEAMMATE.BAT and READ ME FIRST.
  Teammates DOUBLE-CLICK INSTALL_FOR_TEAMMATE.BAT. Done.
  (They do NOT need the source folder, bin, obj or the .NET SDK.)
  RUN_2_TEAMMATE_TRUST_CERT.bat - only trusts the certificate (the
  team installer already does this).

COMMAND LINE (no Revit)
  Export Snapshot once, then:
    CadQC --snapshot model.json --cad "D:\Project\CAD" --out "D:\QC"
  Same reports and markups. Exit code 1 when there are critical issues.
  Build it: dotnet publish src\CadQC.Cli -c Release -o dist\cli

TROUBLESHOOT
- DLL not updating: Revit was open during copy. BUILD_AND_INSTALL
  blocks this and verifies the copy by hash + signature.
- Script window closes / "$PSScriptRoot empty": the script TEXT was
  pasted into PowerShell. Double-click the RUN_ .bat instead.
- "Could not read <file>.dwg": the built-in reader failed on that DWG -
  install ODA File Converter as fallback, or save the DWG as 2018.
- "Could not tell which floor ...": name the file e.g. "2ND FLOOR
  PLAN.dwg" or add a floor mapping row.
- "No double-line walls found": the consultant's wall layers are not
  known - Layer Mapping, set them to Wall, Save profile.
- "Alignment uncertain": no common grid names and too few matching
  walls. Name the grids the same as Revit, or set a manual offset.
- Many "Extra in Revit" on one floor: wrong CAD mapped to that level -
  check the floor list at the top of QC_Report.html.

KNOWN LIMITS (v1.0)
  - Plan (2D) checks at the cut height (1200 mm). Heights, sills and
    sections are not compared with CAD elevations.
  - Unbound xrefs are reported and skipped - bind them or put the xref
    DWGs in the CAD folder.
  - Dynamic blocks are recognised by layer, swing arc and linework
    (their anonymous block names lose the original name).
  - Curved walls compared chord by chord (~500 mm).
  - Single-line walls (no thickness drawn) are reported, not compared.
  - Tested on synthetic plans with planted errors; first real project
    run is the pilot - send the report back for tuning.

PROJECT LAYOUT
  RUN_*.bat / *.ps1        build, install, team package
  src\CadQC.Core\          engine, no Revit dependency
    Dxf\ Conversion\ Extraction\ Alignment\ Compare\ Report\ Engine\
  src\CadQC.Revit\         add-in: ribbon, model reading, QC views,
                           overrides, Issue Browser
  src\CadQC.Cli\           command-line runner
  tests\CadQC.Tests\       tests (synthetic 2nd floor with planted errors)
  config\                  default settings
  docs\sample-output\      every output made from the test drawing
