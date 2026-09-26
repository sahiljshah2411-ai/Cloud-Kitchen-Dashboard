using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RevitCadQC.Core.Settings
{
    /// <summary>
    /// All QC rules and tolerances. Saved as JSON next to the Revit model (or anywhere) so each
    /// project can keep its own layer standards and tolerances. Every length is in millimetres.
    /// </summary>
    public sealed class QcSettings
    {
        // ---------- Input / output ----------
        public string CadFolder { get; set; } = "";
        public bool IncludeSubfolders { get; set; } = true;
        public string OutputFolder { get; set; } = "";
        /// <summary>Path to ODAFileConverter.exe. Empty = auto-detect.</summary>
        public string OdaConverterPath { get; set; } = "";
        /// <summary>Path to accoreconsole.exe (AutoCAD). Used for DWG conversion if ODA is missing. Empty = auto-detect.</summary>
        public string AcCoreConsolePath { get; set; } = "";
        public string DxfOutputVersion { get; set; } = "ACAD2018";

        /// <summary>"auto", "mm", "cm", "m", "in", "ft".</summary>
        public string CadUnits { get; set; } = "auto";
        public bool SkipFrozenAndOffLayers { get; set; } = true;

        // ---------- Floors ----------
        public bool AutoMapFloors { get; set; } = true;
        public List<FloorMapping> FloorMappings { get; set; } = new List<FloorMapping>();
        /// <summary>Plan cut height above the level used to decide which Revit elements belong to a floor.</summary>
        public double CutPlaneHeight { get; set; } = 1200;
        /// <summary>Split a DWG that contains several floor plans side by side using their titles.</summary>
        public bool SplitMultiPlanDrawings { get; set; } = true;

        // ---------- Alignment ----------
        [JsonConverter(typeof(StringEnumConverter))]
        public AlignmentMode Alignment { get; set; } = AlignmentMode.Auto;
        public double AlignmentAcceptRms { get; set; } = 30;

        // ---------- Layer standards (regular expressions, case-insensitive) ----------
        public string WallLayers { get; set; } = @"WALL|MASONRY|BRICK|BLOCK|PARTITION|A-WALL|RCC.?WALL|SHEAR|CORE.?WALL|DRY.?WALL|GYPSUM|AAC";
        public string DoorLayers { get; set; } = @"DOOR|A-DOOR|DR\b|SHUTTER";
        public string WindowLayers { get; set; } = @"WIN|WINDOW|A-GLAZ|GLAZ|VENT|\bV\b|LOUV|FIXED.?GLASS";
        public string ColumnLayers { get; set; } = @"COL|COLUMN|S-COLS|PILLAR|RCC.?COL";
        public string GridLayers { get; set; } = @"GRID|AXIS|S-GRID|C.?L\b|CENTER.?LINE|CENTRE.?LINE";
        public string RoomLayers { get; set; } = @"ROOM|AREA.?IDEN|A-AREA|SPACE|NAME|TEXT|ANNO|A-ANNO";
        public string DimensionLayers { get; set; } = @"DIM|A-ANNO-DIMS";
        public string ExcludeLayers { get; set; } = @"FURN|FURNITURE|HATCH|PATTERN|TITLE|BORDER|DEFPOINTS|FIXTURE|FLOORING|TILE|LANDSCAPE|TREE|CAR|VIEWPORT|VPORT|NPLT|NO.?PLOT|SANITARY|ELEC|PLUMB|HVAC";
        public string DoorBlockNames { get; set; } = @"DOOR|\bDR|^D\d|SHUTTER|SLIDING|ROLLING";
        public string WindowBlockNames { get; set; } = @"WIN|WINDOW|^W\d|^V\d|VENT|LOUV|GLAZ";
        public string ColumnBlockNames { get; set; } = @"COL|COLUMN|PILLAR";
        public List<string> RoomKeywords { get; set; } = new List<string>
        {
            "BEDROOM","BED ROOM","MASTER","BED","KITCHEN","TOILET","TOI","WC","W.C","BATH","BATHROOM","LIVING","DINING","DRAWING",
            "HALL","LOBBY","PASSAGE","CORRIDOR","FOYER","BALCONY","BALC","DECK","UTILITY","WASH","STORE","STORAGE","PUJA","POOJA",
            "PANTRY","STUDY","OFFICE","CABIN","CONFERENCE","MEETING","RECEPTION","WAITING","STAIR","STAIRCASE","LIFT","ELEVATOR","SHAFT",
            "DUCT","ELEC","ELECTRICAL","SERVER","PARKING","TERRACE","SIT OUT","SITOUT","VERANDAH","VERANDA","OTS","DRESS","DRESSING",
            "FAMILY","GUEST","SERVANT","MAID","DRIVER","GYM","POOL","CLUB","SHOP","RETAIL","LAUNDRY","ENTRANCE","ENTRY","TOILET BLOCK",
            "POWDER","STAFF","SECURITY","GUARD","PUMP","METER","GENERATOR","DG","AHU","MECHANICAL","CAFE","CAFETERIA","LOUNGE","LIBRARY",
            "CLASSROOM","LAB","WARD","OT","ICU","NURSE","DUMB WAITER","REFUGE","FIRE","HUB","CORE"
        };

        // ---------- Wall extraction ----------
        public double MinWallThickness { get; set; } = 75;
        public double MaxWallThickness { get; set; } = 750;
        public double ParallelAngleTolDeg { get; set; } = 1.0;
        public double MinSegmentLength { get; set; } = 40;
        public double MinPairOverlap { get; set; } = 120;
        public double CollinearTol { get; set; } = 3;
        public double MergeGapTol { get; set; } = 15;
        /// <summary>Max distance of a thickness note (e.g. "230 THK") from the wall it describes.</summary>
        public double AnnotationSearchRadius { get; set; } = 1500;

        // ---------- Tolerances ----------
        public double WallThicknessTol { get; set; } = 5;
        public double WallPositionTolMinor { get; set; } = 10;
        public double WallPositionTolMajor { get; set; } = 50;
        public double WallMatchSearch { get; set; } = 400;
        public double MinIssueLength { get; set; } = 300;
        public double LengthTol { get; set; } = 25;

        /// <summary>"Auto" = pass if CAD thickness equals Revit total OR core width, "Total", or "Core".</summary>
        public string RevitWallWidthMode { get; set; } = "Auto";
        /// <summary>Accepted CAD↔Revit thickness pairs, e.g. CAD 230 drawn as Revit 250 (230 + 2×10 plaster).</summary>
        public List<ThicknessEquivalent> ThicknessEquivalents { get; set; } = new List<ThicknessEquivalent>();

        public double OpeningSearchRadius { get; set; } = 600;
        public double OpeningPositionTol { get; set; } = 50;
        public double OpeningWidthTol { get; set; } = 20;

        public double ColumnSearchRadius { get; set; } = 500;
        public double ColumnPositionTol { get; set; } = 15;
        public double ColumnSizeTol { get; set; } = 10;
        public double ColumnRotationTolDeg { get; set; } = 1.0;

        public double GridOffsetTol { get; set; } = 5;
        public double GridAngleTolDeg { get; set; } = 0.05;

        public double DimensionTol { get; set; } = 10;
        public double DimensionSnapDistance { get; set; } = 60;

        public double RoomSizeTol { get; set; } = 50;
        public double RoomAreaTolPercent { get; set; } = 3;

        // ---------- Checks ----------
        public bool CheckWalls { get; set; } = true;
        public bool CheckDoors { get; set; } = true;
        public bool CheckWindows { get; set; } = true;
        public bool CheckColumns { get; set; } = true;
        public bool CheckRooms { get; set; } = true;
        public bool CheckGrids { get; set; } = true;
        public bool CheckDimensions { get; set; } = true;
        public bool CheckWallTypeNames { get; set; } = true;
        public bool CheckRevitDuplicates { get; set; } = true;
        public bool CheckCadDrafting { get; set; } = true;
        public bool ReportExtraRevitElements { get; set; } = true;

        // ---------- Outputs ----------
        public bool WriteHtmlReport { get; set; } = true;
        public bool WriteCsv { get; set; } = true;
        public bool WriteExcelXml { get; set; } = true;
        public bool WriteJson { get; set; } = true;
        public bool WriteCadMarkupDxf { get; set; } = true;
        public bool WriteAutoCadScript { get; set; } = true;
        public bool ConvertMarkupToDwg { get; set; } = true;
        public bool IncludeRevitOverlayInCadMarkup { get; set; } = true;

        // ---------- Revit highlighting ----------
        public bool IncludeLinkedModels { get; set; } = false;
        /// <summary>Also check the DWG/DXF files linked in the Revit model, using their placement as alignment.</summary>
        public bool UseModelCadLinks { get; set; } = true;
        /// <summary>CAD files outside the CAD folder (e.g. from model links), added at run time.</summary>
        [JsonIgnore]
        public List<string> ExtraCadFiles { get; set; } = new List<string>();
        public bool CreateQcViews { get; set; } = true;
        public bool LinkCadIntoQcViews { get; set; } = true;
        public bool Create3dQcView { get; set; } = true;
        public bool WriteIssueIdsToComments { get; set; } = false;

        // ---------- persistence ----------

        public static QcSettings Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new QcSettings();
            var s = JsonConvert.DeserializeObject<QcSettings>(File.ReadAllText(path), JsonOptions()) ?? new QcSettings();
            return s;
        }

        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonConvert.SerializeObject(this, JsonOptions()));
        }

        public static JsonSerializerSettings JsonOptions() => new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
            NullValueHandling = NullValueHandling.Ignore
        };

        public double CadUnitsToMm(double detected)
        {
            switch ((CadUnits ?? "auto").Trim().ToLowerInvariant())
            {
                case "mm": return 1;
                case "cm": return 10;
                case "m": return 1000;
                case "in": case "inch": case "inches": return 25.4;
                case "ft": case "feet": return 304.8;
                default: return detected;
            }
        }

        private readonly Dictionary<string, Regex> _rx = new Dictionary<string, Regex>();

        public bool Matches(string pattern, string value)
        {
            if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrEmpty(value)) return false;
            if (!_rx.TryGetValue(pattern, out var r))
            {
                try { r = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
                catch (ArgumentException) { r = new Regex(Regex.Escape(pattern), RegexOptions.IgnoreCase); }
                _rx[pattern] = r;
            }
            return r.IsMatch(value);
        }

        /// <summary>Runtime layer classifier (profile / dictionary / built-in rules). Not saved in the JSON.</summary>
        [JsonIgnore]
        public LayerClassifier Layers
        {
            get => _layers ?? (_layers = LayerClassifier.FromSettings(this));
            set => _layers = value;
        }
        private LayerClassifier _layers;

        /// <summary>Layer mapping confirmed in the review grid for this run only ("Run without saving").</summary>
        [JsonIgnore]
        public CadLayerProfile SessionProfile { get; set; }

        public bool IsLayer(LayerCategory category, string layer) => Layers.Classify(layer) == category;

        public bool IsExcluded(string layer) => Layers.Classify(layer) == LayerCategory.Ignore;
    }

    public enum AlignmentMode
    {
        /// <summary>Try Revit shared/identity placement, then grid names, then geometric best fit.</summary>
        Auto,
        Identity,
        Grids,
        BestFit,
        Manual
    }

    public sealed class FloorMapping
    {
        /// <summary>DWG/DXF file name (or part of it).</summary>
        public string CadFile { get; set; }
        /// <summary>Revit level name.</summary>
        public string LevelName { get; set; }
        /// <summary>Optional plan title inside a multi-plan DWG (e.g. "SECOND FLOOR PLAN").</summary>
        public string RegionTitle { get; set; }
        public bool Enabled { get; set; } = true;
        /// <summary>Manual CAD→Revit transform, used when Alignment = Manual or auto alignment fails.</summary>
        public double? ManualRotationDeg { get; set; }
        public double? ManualOffsetX { get; set; }
        public double? ManualOffsetY { get; set; }
        /// <summary>Created at run time from a CAD link in the Revit model (not saved).</summary>
        [JsonIgnore]
        public bool FromModelLink { get; set; }
    }

    public sealed class ThicknessEquivalent
    {
        public double Cad { get; set; }
        public List<double> Revit { get; set; } = new List<double>();
    }
}
