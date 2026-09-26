REVIT CAD QC (CAD vs 3D TECHNICAL QC) - Revit 2022 / 2023 / 2024 / 2025 / 2026
=====================================================================

WHAT IT IS
  Checks the Revit model against the CAD plans it was built from,
  element by element, and marks every difference in Revit, in CAD and
  in a report. Point it at a folder of DWG/DXF plans (and/or use the
  CAD already linked in the model). DWG is read directly - nothing is
  exported or converted by hand.

QUICK START (YOU)
  1. Unzip. Close ALL Revit windows.
  2. Double-click RUN_1_BUILD_AND_INSTALL.bat (click YES on the
     certificate dialog the first time).
  3. Open Revit -> tab "CAD QC" -> Run CAD QC -> pick the CAD folder.

TAB: "CAD QC"
  panel "Technical QC"
  Run CAD QC        CAD folder / CAD links -> (layer review, only for a
                    new consultant) -> checks every floor -> QC views,
                    Issue Browser, HTML + Excel report, CAD markups
  Issue Browser     list of issues; double-click = open QC plan, zoom,
                    select elements. "Accept" = not an error, stays
                    closed on the next run
  Layer Mapping     review / correct which CAD layers are walls, doors,
                    windows, columns, grids, rooms. Saved per consultant
  Clear QC Views    delete every "QC - ..." view with its clouds and
                    linked CAD, clear QC comments
  panel "Setup"
  Settings          this project's settings (<model>_CadQC.json)
  Layer Dictionary  CadLayerDictionary.txt in Notepad
  Export Snapshot   model data to JSON for the command-line runner
  Version           version, notes, paths, dictionary, profiles in use
  Check for Update  pull the newest build from the team package folder

WHAT IT CHECKS (per floor)
  WALLS    missing / partly missing / extra in Revit / thickness
           (CAD 230 drawn or "230 THK" note vs Revit 200) / position
           (both faces reported) / modelled as curtain wall.
           Revit total OR core width accepted (RevitWallWidthMode),
           accepted pairs e.g. CAD 230 = Revit 254 (THICKNESS_EQUIV)
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
  CAD linked in the model: the link's level / plan view decides.
  Anything it cannot match is listed in the run log - add a row in the
  floor mapping (CAD file, optional plan title, Revit level).

HOW CAD IS LINED UP WITH THE MODEL (no setup)
  1. CAD linked in the model - the link's own placement (exact)
  2. identity (CAD already in model coordinates)
  3. grid names (A, B, 1, 2... intersections) - exact
  4. best fit: extents x 4 rotations + ICP on wall centre lines
  The one that puts most CAD walls on Revit walls wins. Poor fit ->
  CRITICAL "Alignment uncertain" issue, never silent. Manual offset/
  rotation per floor: ManualOffsetX / ManualOffsetY / ManualRotationDeg.

CAD LAYERS - WHICH LAYER IS WHAT (never guessed)
  1. Consultant profile - exact layer names, confirmed ONCE in the
     review grid, then remembered:  %APPDATA%\RevitCadQC\CadProfiles
     (outside the tool folder - reinstalling never deletes them; the team
     package carries them). A drawing uses the profile that knows >= 60%
     of its layers. Tick "Review the CAD layer mapping" in Run CAD QC to
     look again and overwrite.
  2. CadLayerDictionary.txt - token rules, Notepad-editable, FIRST MATCH
     WINS (IGNORE before WALL so "A-WALL-PATT" is not a wall).
     Your copy: %APPDATA%\RevitCadQC\CadLayerDictionary.txt
     Shipped copy next to RevitCadQC.dll. Missing/bad file = built-in.
  3. Unknown layers are skipped and listed in the run log.
  If nothing is classed as wall, wall layers are detected from how the
  lines behave (parallel pairs at wall spacing) and reported.

CENTRAL OFFICE RULES - CadQcConfig.txt (BIM lead only)
  KEY=VALUE   office default (a project may change it)
  !KEY=VALUE  ENFORCED for everybody
  KEY = any QC setting (tolerances, layer patterns, checks) plus
  THICKNESS_EQUIV=230|254 and ROOM_KEYWORD=MUMTY.
  Read LIVE from the team package folder, so a change there reaches the
  whole team on the next QC run - no rebuild, no reinstall.
  Order: team package -> %APPDATA%\RevitCadQC -> next to the DLL.
  Bad lines are ignored and listed in the run log.

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
            <file>_QC_markup.dwg  same markup plus the Revit walls /
                                  columns / openings on layers QC_REVIT_*
                                  - XREF or INSERT at 0,0 (+ .dxf copy)
  Report  QC_Report.html   summary + zoomable plan per floor + filters
          QC_Issues.xlsx   issue register for Excel
          QC_Issues.csv
          qc_report.json + history\  - issues marked NEW / OPEN /
                                       ACCEPTED / RESOLVED run to run

SETTINGS
  Per project: <model folder>\<model>_CadQC.json (Settings button).
  Office default: %APPDATA%\RevitCadQC\default_settings.json - every
  new project starts from it; the team package ships it.
  Units: detected from the header AND checked against wall spacing
  (a mm header on a drawing in metres or inches is caught).
  Full list with defaults: config\qc-settings.sample.json

BUILD + INSTALL (YOU)
  RUN_1_BUILD_AND_INSTALL.bat - ONE script: dotnet check (installs a
    user-local .NET 8 SDK if missing, no admin), unit tests, build every
    Revit version installed on this PC, certificate, sign, install,
    verify by hash + signature. Run after every change.
    (Refuses to run while Revit is open.)
    Only some versions:  RUN_1_BUILD_AND_INSTALL.bat -Versions 2022,2025
  RUN_BUILD_ONLY_R2025.bat / _R2022 / _R2026 - build only, Revit may
    stay open, for the Add-in Manager (Faceless) loop.
  RUN_3_UNINSTALL.bat - remove from every Revit version.
  Revit itself is NOT needed to build (Revit API from NuGet).

RELEASE TO TEAM
  1. Bump Version / UpdatedOn / notes in Core\ToolVersion.cs.
  2. RUN_1_BUILD_AND_INSTALL.bat
  3. RUN_4_MAKE_TEAM_PACKAGE.bat -> RevitCadQC_TEAM_PACKAGE (+ .zip) on
     Desktop: signed R20xx DLLs, .addin, certificate, version.txt
     (FORCE = silent update at Revit start; -NoForce = popup only),
     CadQcConfig.txt, your CadLayerDictionary.txt, every consultant
     profile, office default settings, INSTALL_FOR_TEAMMATE.bat,
     READ ME FIRST.txt. Copied to every folder in TEAM_DEPLOY_PATHS.txt.
  4. New teammates: DOUBLE-CLICK INSTALL_FOR_TEAMMATE.bat. Existing
     teammates: restart Revit (FORCE) or click CHECK FOR UPDATE.
  version.txt may carry NEWPATH=<folder> to move the package; tools
  follow it and remember the new folder.
  RUN_2_TEAMMATE_TRUST_CERT.bat - only trusts the certificate.

DWG READING - NOTHING TO INSTALL
  DWG is read directly by ACadSharp 3.6.35 (built in, same pinned
  version as AashirTools, so both add-ins load side by side without a
  clash). Fallbacks, used only if the built-in reader fails: ODA File
  Converter (free) or AutoCAD's accoreconsole.exe, found automatically.
  Readings are cached in _RevitCadQC\.dxf-cache and only redone when
  the DWG changes.

COMMAND LINE (no Revit)
  Export Snapshot once, then:
    CadQC --snapshot model.json --cad "D:\Project\CAD" --out "D:\QC"
  Same reports and markups. Exit code 1 when there are critical issues.
  Build it: dotnet publish Cli\RevitCadQC.Cli.csproj -c Release -o dist\cli

TROUBLESHOOT
- DLL not updating: Revit was open during copy. BUILD_AND_INSTALL
  blocks this and verifies the copy by hash + signature.
- Script window closes / "$PSScriptRoot empty": the script TEXT was
  pasted into PowerShell. Double-click the RUN_ .bat instead.
- "Could not read <file>.dwg": the built-in reader failed on that DWG -
  install ODA File Converter as fallback, or save the DWG as 2018.
- "Could not tell which floor ...": name the file e.g. "2ND FLOOR
  PLAN.dwg", link it in the level's plan view, or add a floor mapping.
- "No double-line walls found": the consultant's wall layers are not
  known - Layer Mapping, set them to Wall, Save profile.
- "Alignment uncertain": no common grid names and too few matching
  walls. Link the CAD in Revit at the right place, name the grids the
  same, or set a manual offset.
- Many "Extra in Revit" on one floor: wrong CAD mapped to that level -
  check the floor list at the top of QC_Report.html.
- Two CAD QC tabs / double load: an older CadQC.addin is left in
  %APPDATA%\Autodesk\Revit\Addins\<ver> - RUN_1 removes it.

KNOWN LIMITS (v1.1)
  - Plan (2D) checks at the cut height (1200 mm). Heights, sills and
    sections are not compared with CAD elevations.
  - Unbound xrefs are reported and skipped - bind them or put the xref
    DWGs in the CAD folder.
  - Dynamic blocks are recognised by layer, swing arc and linework
    (their anonymous block names lose the original name).
  - Curved walls compared chord by chord (~500 mm).
  - Single-line walls (no thickness drawn) are reported, not compared.
  - Tested on synthetic plans with planted errors and on DWG written by
    ACadSharp; the first real project run is the pilot - send the
    report back for tuning.

PROJECT LAYOUT
  RevitCadQC.csproj       the add-in -> bin\R20xx\RevitCadQC.dll
  RevitCadQC.addin        manifest
  App.cs                  ribbon + start-up update check
  Commands\               one file per button
  Core\                   engine: Dxf, Conversion (DWG), Extraction,
                          Alignment, Compare, Report, Engine, Settings,
                          ToolVersion, UpdateCore
  Core\Revit\             model reading, QC views, CAD links, navigation
  UI\                     windows (Run dialog, Layer review, Issue
                          Browser, progress)
  Tests\                  41 unit tests (synthetic plans, DWG, profiles,
                          config, update, CAD links)
  Cli\                    command-line runner
  CadLayerDictionary.txt  layer rules          CadQcConfig.txt  office rules
  TEAM_DEPLOY_PATHS.txt   network folders for the team package
  config\                 default settings     docs\sample-output\  examples
  RUN_*.bat / *.ps1       build, sign, install, team package
