using System.Collections.Generic;
using RevitCadQC.Core.Geometry;

namespace RevitCadQC.Core.Dxf
{
    /// <summary>
    /// Flattened, world-space view of a DXF drawing. Blocks are exploded recursively so every
    /// piece of geometry sits in model-space coordinates with its effective layer resolved.
    /// Units are the drawing's own units; <see cref="UnitToMm"/> converts them to millimetres.
    /// </summary>
    public sealed class CadDrawing
    {
        public string SourcePath { get; set; }
        public string DxfVersion { get; set; }
        public int InsUnits { get; set; }
        public double UnitToMm { get; set; } = 1.0;

        public List<CadLayer> Layers { get; } = new List<CadLayer>();
        public List<CadCurve> Curves { get; } = new List<CadCurve>();
        public List<CadArc> Arcs { get; } = new List<CadArc>();
        public List<CadText> Texts { get; } = new List<CadText>();
        public List<CadInsert> Inserts { get; } = new List<CadInsert>();
        public List<CadDimension> Dimensions { get; } = new List<CadDimension>();
        public List<string> Warnings { get; } = new List<string>();

        public Box2 Extents()
        {
            var b = Box2.Empty;
            foreach (var c in Curves)
                foreach (var p in c.Points) b = b.Include(p);
            return b;
        }

        /// <summary>Scales every coordinate and length in place (used to convert drawing units to millimetres).</summary>
        public void ScaleInPlace(double k)
        {
            if (System.Math.Abs(k - 1.0) < 1e-12) return;
            var s = Affine2.Scale(k, k);
            foreach (var c in Curves) for (int i = 0; i < c.Points.Count; i++) c.Points[i] = c.Points[i] * k;
            foreach (var a in Arcs) { a.Center = a.Center * k; a.Radius *= k; }
            foreach (var t in Texts) { t.Position = t.Position * k; t.Height *= k; }
            foreach (var ins in Inserts)
            {
                ins.Position = ins.Position * k;
                ins.Transform = s.Compose(ins.Transform);
            }
            foreach (var d in Dimensions)
            {
                d.DefPoint1 = d.DefPoint1 * k; d.DefPoint2 = d.DefPoint2 * k;
                d.DimLinePoint = d.DimLinePoint * k; d.TextPosition = d.TextPosition * k;
                if (d.Measurement.HasValue) d.Measurement = d.Measurement.Value * k;
            }
            UnitToMm = 1.0;
        }
    }

    public sealed class CadLayer
    {
        public string Name { get; set; }
        public int Color { get; set; }
        public bool Frozen { get; set; }
        public bool Off { get; set; }
    }

    public abstract class CadEntity
    {
        /// <summary>Effective layer (block content on layer "0" inherits the insert's layer).</summary>
        public string Layer { get; set; }
        public string Handle { get; set; }
        /// <summary>Name of the block this entity came from (innermost), or null for model space.</summary>
        public string BlockName { get; set; }
        /// <summary>Index into <see cref="CadDrawing.Inserts"/> of the insert (block reference) that directly owns this entity, or -1 for model space.
        /// For a <see cref="CadInsert"/> it is the parent insert, so nesting can be walked upwards.</summary>
        public int InsertIndex { get; set; } = -1;
        public string EntityType { get; set; }
    }

    /// <summary>Polyline-like geometry (LINE, LWPOLYLINE, POLYLINE, hatch boundaries, tessellated arcs).</summary>
    public sealed class CadCurve : CadEntity
    {
        public List<Vec2> Points { get; set; } = new List<Vec2>();
        public bool Closed { get; set; }

        public IEnumerable<Seg2> Segments()
        {
            for (int i = 0; i + 1 < Points.Count; i++) yield return new Seg2(Points[i], Points[i + 1]);
            if (Closed && Points.Count > 2) yield return new Seg2(Points[Points.Count - 1], Points[0]);
        }
    }

    public sealed class CadArc : CadEntity
    {
        public Vec2 Center { get; set; }
        public double Radius { get; set; }
        /// <summary>Radians, CCW.</summary>
        public double StartAngle { get; set; }
        public double EndAngle { get; set; }
        public bool IsCircle { get; set; }

        public double Sweep
        {
            get
            {
                if (IsCircle) return 2 * System.Math.PI;
                double s = EndAngle - StartAngle;
                while (s <= 0) s += 2 * System.Math.PI;
                return s;
            }
        }

        public Vec2 StartPoint => Center + Vec2.FromAngle(StartAngle) * Radius;
        public Vec2 EndPoint => Center + Vec2.FromAngle(EndAngle) * Radius;
    }

    public sealed class CadText : CadEntity
    {
        public string Text { get; set; }
        public Vec2 Position { get; set; }
        public double Height { get; set; }
        public double Rotation { get; set; }
        public bool IsAttribute { get; set; }
        public string AttributeTag { get; set; }
    }

    public sealed class CadInsert : CadEntity
    {
        public string Name { get; set; }
        public Vec2 Position { get; set; }
        public double ScaleX { get; set; } = 1;
        public double ScaleY { get; set; } = 1;
        public double Rotation { get; set; }
        public Affine2 Transform { get; set; }
        public int Depth { get; set; }
        /// <summary>Block-local bounding box of the block definition (drawing units).</summary>
        public Box2 LocalBounds { get; set; }
        /// <summary>Radii of arcs found inside the block (block-local units), used to size door leaves.</summary>
        public List<double> LocalArcRadii { get; set; } = new List<double>();
        public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>();
    }

    public sealed class CadDimension : CadEntity
    {
        public int DimType { get; set; }
        public Vec2 DefPoint1 { get; set; }
        public Vec2 DefPoint2 { get; set; }
        public Vec2 DimLinePoint { get; set; }
        public Vec2 TextPosition { get; set; }
        /// <summary>Radians. For rotated dimensions this is the measurement direction.</summary>
        public double Angle { get; set; }
        public double? Measurement { get; set; }
        public string TextOverride { get; set; }
    }
}
