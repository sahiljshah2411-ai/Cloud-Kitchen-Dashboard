using System;
using System.Collections.Generic;
using CadQC.Core.Geometry;

namespace CadQC.Core.Model
{
    // Source-neutral element model. Both the CAD extractors and the Revit extractor produce these
    // objects (all lengths in millimetres, all positions in the Revit internal XY frame once aligned),
    // so the comparison engine never needs to know where the data came from.

    public sealed class QcLevel
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public double ElevationMm { get; set; }
        public bool IsBuildingStory { get; set; } = true;
    }

    public sealed class QcWall
    {
        public string Id { get; set; }
        public Seg2 Centerline { get; set; }
        /// <summary>Total thickness (Revit: full type width, CAD: face-to-face distance).</summary>
        public double Thickness { get; set; }
        /// <summary>Revit only: thickness of the structural core (excluding finishes).</summary>
        public double? CoreThickness { get; set; }
        /// <summary>CAD only: thickness written in a nearby note (e.g. "230 THK").</summary>
        public double? AnnotatedThickness { get; set; }
        public string AnnotationText { get; set; }
        public string TypeName { get; set; }
        public string Function { get; set; }
        public bool IsCurtain { get; set; }
        public bool IsStructural { get; set; }
        public bool IsArcSegment { get; set; }
        public double BaseElevationMm { get; set; }
        public double TopElevationMm { get; set; }
        public List<string> Levels { get; set; } = new List<string>();
        public string BaseLevel { get; set; }
        public string Layer { get; set; }
        public List<string> Handles { get; set; } = new List<string>();

        public double Length => Centerline?.Length ?? 0;
        public override string ToString() => $"Wall {Id} t={Thickness:0} {Centerline}";
    }

    public enum OpeningKind { Door, Window, Opening }

    public sealed class QcOpening
    {
        public string Id { get; set; }
        public OpeningKind Kind { get; set; }
        public Vec2 Position { get; set; }
        public double Width { get; set; }
        public double? Height { get; set; }
        public double? SillHeight { get; set; }
        /// <summary>Undirected direction of the host wall (radians).</summary>
        public double Angle { get; set; }
        public string Mark { get; set; }
        public string TypeMark { get; set; }
        public string TypeName { get; set; }
        public string FamilyName { get; set; }
        public string HostWallId { get; set; }
        public string LevelName { get; set; }
        public string Layer { get; set; }
        public string BlockName { get; set; }
        public string Handle { get; set; }
        public string Tag { get; set; }
        public string WidthSource { get; set; }
        public override string ToString() => $"{Kind} {Id} w={Width:0} at {Position}";
    }

    public sealed class QcColumn
    {
        public string Id { get; set; }
        public Vec2 Center { get; set; }
        public double Width { get; set; }
        public double Depth { get; set; }
        public double Angle { get; set; }
        public bool IsCircular { get; set; }
        public bool IsStructural { get; set; }
        public string TypeName { get; set; }
        public string LevelName { get; set; }
        public List<string> Levels { get; set; } = new List<string>();
        public string Layer { get; set; }
        public string Handle { get; set; }
        public string Mark { get; set; }
        public List<Vec2> Outline { get; set; } = new List<Vec2>();

        public double MinSize => Math.Min(Width, Depth);
        public double MaxSize => Math.Max(Width, Depth);
        public override string ToString() => IsCircular ? $"Column Ø{Width:0} at {Center}" : $"Column {Width:0}x{Depth:0} at {Center}";
    }

    public sealed class QcRoom
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Number { get; set; }
        public string LevelName { get; set; }
        public Vec2 Position { get; set; }
        /// <summary>Revit: outer boundary loop. CAD: empty.</summary>
        public List<Vec2> Boundary { get; set; } = new List<Vec2>();
        /// <summary>Square metres.</summary>
        public double? AreaM2 { get; set; }
        /// <summary>CAD: size written under the room name, e.g. "3000 X 3600" or 10'0"x12'0" (mm).</summary>
        public double? SizeA { get; set; }
        public double? SizeB { get; set; }
        public string SizeText { get; set; }
        public string Layer { get; set; }
        public string Handle { get; set; }
        public bool IsPlaced { get; set; } = true;
        public bool IsEnclosed { get; set; } = true;
    }

    public sealed class QcGrid
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public Seg2 Line { get; set; }
        public string Layer { get; set; }
        public string Handle { get; set; }
    }

    public sealed class QcDimension
    {
        public Vec2 P1 { get; set; }
        public Vec2 P2 { get; set; }
        /// <summary>Unit direction of measurement.</summary>
        public Vec2 Direction { get; set; }
        public double ValueMm { get; set; }
        public double GeometricMm { get; set; }
        public string Text { get; set; }
        public bool IsOverridden { get; set; }
        public Vec2 TextPosition { get; set; }
        public string Layer { get; set; }
        public string Handle { get; set; }
    }

    /// <summary>Everything the Revit add-in exports from the model. Serialisable so QC can also run outside Revit.</summary>
    public sealed class RevitSnapshot
    {
        public string ProjectName { get; set; }
        public string DocumentPath { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public string RevitVersion { get; set; }
        public List<QcLevel> Levels { get; set; } = new List<QcLevel>();
        public List<QcWall> Walls { get; set; } = new List<QcWall>();
        public List<QcOpening> Openings { get; set; } = new List<QcOpening>();
        public List<QcColumn> Columns { get; set; } = new List<QcColumn>();
        public List<QcRoom> Rooms { get; set; } = new List<QcRoom>();
        public List<QcGrid> Grids { get; set; } = new List<QcGrid>();
    }

    /// <summary>Everything extracted from one CAD plan (one floor), in CAD drawing coordinates (mm).</summary>
    public sealed class CadFloorData
    {
        public string SourceFile { get; set; }
        public string RegionTitle { get; set; }
        public Box2 Bounds { get; set; }
        public List<QcWall> Walls { get; set; } = new List<QcWall>();
        public List<QcOpening> Openings { get; set; } = new List<QcOpening>();
        public List<QcColumn> Columns { get; set; } = new List<QcColumn>();
        public List<QcRoom> Rooms { get; set; } = new List<QcRoom>();
        public List<QcGrid> Grids { get; set; } = new List<QcGrid>();
        public List<QcDimension> Dimensions { get; set; } = new List<QcDimension>();
        /// <summary>Raw wall-layer segments (for overlays and drafting checks).</summary>
        public List<Seg2> WallLayerSegments { get; set; } = new List<Seg2>();
        /// <summary>Unpaired wall-layer lines (drafting problems or single-line walls).</summary>
        public List<Seg2> UnpairedWallLines { get; set; } = new List<Seg2>();
        public List<string> WallLayersUsed { get; set; } = new List<string>();
        public List<string> Notes { get; set; } = new List<string>();
    }
}
