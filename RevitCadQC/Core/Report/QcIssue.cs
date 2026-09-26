using System;
using System.Collections.Generic;
using RevitCadQC.Core.Geometry;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RevitCadQC.Core.Report
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum Severity { Critical = 0, Major = 1, Minor = 2, Info = 3 }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum IssueStatus { New, Open, Accepted, Resolved }

    public static class IssueCategory
    {
        public const string Wall = "Wall";
        public const string Door = "Door";
        public const string Window = "Window";
        public const string Column = "Column";
        public const string Room = "Room";
        public const string Grid = "Grid";
        public const string Dimension = "Dimension";
        public const string Alignment = "Alignment";
        public const string CadDrafting = "CAD Drafting";
        public const string ModelHygiene = "Revit Model";
    }

    public static class IssueType
    {
        public const string MissingInRevit = "Missing in Revit";
        public const string PartiallyMissing = "Partially missing in Revit";
        public const string ExtraInRevit = "Extra in Revit (not in CAD)";
        public const string ThicknessMismatch = "Thickness mismatch";
        public const string PositionOffset = "Position offset";
        public const string LengthMismatch = "Length mismatch";
        public const string WidthMismatch = "Width mismatch";
        public const string SizeMismatch = "Size mismatch";
        public const string RotationMismatch = "Rotation mismatch";
        public const string KindMismatch = "Category mismatch";
        public const string MarkMismatch = "Mark / tag mismatch";
        public const string NameMismatch = "Name mismatch";
        public const string AreaMismatch = "Area mismatch";
        public const string DimensionMismatch = "Dimension mismatch";
        public const string TypeNameMismatch = "Type name does not match width";
        public const string Duplicate = "Duplicate / overlapping element";
        public const string NotEnclosed = "Room not enclosed / unplaced";
        public const string AnnotationConflict = "Note conflicts with drawn geometry";
        public const string DimensionOverride = "Dimension text overridden";
        public const string UnpairedLine = "Single line on wall layer";
        public const string AlignmentWarning = "Alignment uncertain";
        public const string FloorNotMapped = "Floor not mapped";
    }

    public sealed class QcIssue
    {
        public string Id { get; set; }
        /// <summary>Stable key (type + rounded position) used to track the same issue across QC runs.</summary>
        public string Key { get; set; }
        public string Floor { get; set; }
        public string LevelName { get; set; }
        public string Category { get; set; }
        public string IssueType { get; set; }
        public Severity Severity { get; set; }
        public IssueStatus Status { get; set; } = IssueStatus.New;
        public string Title { get; set; }
        public string Description { get; set; }
        public string CadValue { get; set; }
        public string RevitValue { get; set; }
        public double? Delta { get; set; }

        /// <summary>Location in the Revit internal frame, mm.</summary>
        public Vec2? RevitLocation { get; set; }
        /// <summary>Location in the CAD drawing, drawing units converted to mm.</summary>
        public Vec2? CadLocation { get; set; }
        /// <summary>Geometry to draw in Revit (e.g. the centre line of a missing wall), Revit frame, mm.</summary>
        public List<Vec2> RevitPath { get; set; }
        /// <summary>Same geometry in CAD coordinates (mm).</summary>
        public List<Vec2> CadPath { get; set; }
        /// <summary>Radius of the highlight cloud, mm.</summary>
        public double MarkerRadius { get; set; } = 600;

        public List<string> RevitElementIds { get; set; } = new List<string>();
        public List<string> CadHandles { get; set; } = new List<string>();
        public string CadLayer { get; set; }
        public string CadFile { get; set; }
        public string Comment { get; set; }
        public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;

        public override string ToString() => $"[{Severity}] {Id} {Category}/{IssueType}: {Title}";
    }
}
