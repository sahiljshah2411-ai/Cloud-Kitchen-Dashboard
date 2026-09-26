using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitCadQC.Core.Geometry;
using RevitCadQC.Core.Model;

namespace RevitCadQC.Core.Revit
{
    /// <summary>
    /// Reads the Revit model into the neutral QC model (millimetres, internal coordinates):
    /// walls (true centre line of the full width, not the location line), doors, windows,
    /// columns (measured from their real geometry), rooms (finish boundaries) and grids.
    /// Elements from linked models can be included and are transformed into the host frame.
    /// </summary>
    public sealed class RevitExtractor
    {
        public const double FtToMm = 304.8;
        private readonly Document _doc;
        private readonly double _cutHeightFt;
        private List<(Level level, double cutZ)> _levels;

        public List<string> Warnings { get; } = new List<string>();

        public RevitExtractor(Document doc, double cutPlaneHeightMm)
        {
            _doc = doc;
            _cutHeightFt = cutPlaneHeightMm / FtToMm;
        }

        public RevitSnapshot Extract(bool includeLinks)
        {
            var snap = new RevitSnapshot
            {
                ProjectName = _doc.ProjectInformation?.Name,
                DocumentPath = _doc.PathName,
                RevitVersion = _doc.Application.VersionNumber
            };
            _levels = new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.ProjectElevation)
                .Select(l => (l, l.ProjectElevation + _cutHeightFt)).ToList();
            foreach (var (l, _) in _levels)
            {
                var story = l.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY);
                snap.Levels.Add(new QcLevel
                {
                    Id = Id(l.Id),
                    Name = l.Name,
                    ElevationMm = l.ProjectElevation * FtToMm,
                    IsBuildingStory = story == null || story.AsInteger() == 1
                });
            }

            ReadDocument(_doc, Transform.Identity, "", snap);
            if (includeLinks)
            {
                foreach (var link in new FilteredElementCollector(_doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                {
                    var ld = link.GetLinkDocument();
                    if (ld == null) continue;
                    ReadDocument(ld, link.GetTotalTransform(), "L" + Id(link.Id) + ":", snap);
                }
            }
            return snap;
        }

        private void ReadDocument(Document doc, Transform t, string idPrefix, RevitSnapshot snap)
        {
            ReadWalls(doc, t, idPrefix, snap);
            ReadOpenings(doc, t, idPrefix, snap);
            ReadColumns(doc, t, idPrefix, snap);
            ReadRooms(doc, t, idPrefix, snap);
            if (idPrefix.Length == 0) ReadGrids(doc, snap);
        }

        #region walls

        private void ReadWalls(Document doc, Transform t, string pre, RevitSnapshot snap)
        {
            foreach (var w in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
            {
                try
                {
                    if (!(w.Location is LocationCurve lc) || lc.Curve == null) continue;
                    var type = w.WallType;
                    bool curtain = type.Kind == WallKind.Curtain;
                    double width = w.Width;
                    double? core = null;
                    double offset = 0;
                    var cs = type.GetCompoundStructure();
                    if (cs != null)
                    {
                        int first = cs.GetFirstCoreLayerIndex(), last = cs.GetLastCoreLayerIndex();
                        if (first >= 0 && last >= first)
                        {
                            double c = 0;
                            for (int i = first; i <= last; i++) c += cs.GetLayerWidth(i);
                            core = c * FtToMm;
                        }
                        var key = w.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
                        if (key != null)
                        {
                            try { offset = cs.GetOffsetForLocationLine((WallLocationLine)key.AsInteger()); }
                            catch { offset = 0; }
                        }
                    }
                    // centre of the full wall = location line shifted by the location-line offset. The sign convention is
                    // confirmed against the wall's real geometry so flipped walls and every Location Line setting work.
                    var shift = CentreShift(w, lc.Curve, offset);
                    var levels = LevelsCut(w, t);
                    string baseLevel = LevelName(doc, w.LevelId);
                    var bb = w.get_BoundingBox(null);
                    foreach (var seg in Flatten(lc.Curve))
                    {
                        var a = t.OfPoint(seg.Item1 + shift);
                        var b = t.OfPoint(seg.Item2 + shift);
                        snap.Walls.Add(new QcWall
                        {
                            Id = pre + Id(w.Id),
                            Centerline = new Seg2(P(a), P(b)),
                            Thickness = Math.Round(width * FtToMm, 2),
                            CoreThickness = core.HasValue ? Math.Round(core.Value, 2) : (double?)null,
                            TypeName = type.Name,
                            Function = type.Function.ToString(),
                            IsCurtain = curtain,
                            IsStructural = w.StructuralUsage != Autodesk.Revit.DB.Structure.StructuralWallUsage.NonBearing,
                            IsArcSegment = !(lc.Curve is Line),
                            BaseElevationMm = bb != null ? t.OfPoint(bb.Min).Z * FtToMm : 0,
                            TopElevationMm = bb != null ? t.OfPoint(bb.Max).Z * FtToMm : 0,
                            Levels = levels,
                            BaseLevel = baseLevel
                        });
                    }
                }
                catch (Exception ex)
                {
                    Warnings.Add($"Wall {w.Id}: {ex.Message}");
                }
            }
        }

        private static readonly Options WallGeomOptions = new Options { DetailLevel = ViewDetailLevel.Coarse };

        private static XYZ CentreShift(Wall w, Curve loc, double csOffset)
        {
            var n = w.Orientation;
            if (n == null || n.IsZeroLength()) return XYZ.Zero;
            double? g = null;
            try
            {
                var origin = loc.Evaluate(0.5, true);
                double lo = double.MaxValue, hi = double.MinValue;
                foreach (var o in w.get_Geometry(WallGeomOptions) ?? Enumerable.Empty<GeometryObject>())
                {
                    if (!(o is Solid sd) || sd.Volume <= 1e-9) continue;
                    foreach (Edge e in sd.Edges)
                        foreach (var p in e.Tessellate())
                        {
                            double d = (p - origin).DotProduct(n);
                            lo = Math.Min(lo, d); hi = Math.Max(hi, d);
                        }
                }
                if (lo < hi && loc is Line) g = (lo + hi) / 2;
            }
            catch { g = null; }
            if (Math.Abs(csOffset) < 1e-9) return XYZ.Zero; // location line is already the centre
            if (g == null) return n.Multiply(-csOffset);
            // pick the sign that agrees with the geometry
            return Math.Abs(g.Value - csOffset) < Math.Abs(g.Value + csOffset) ? n.Multiply(csOffset) : n.Multiply(-csOffset);
        }

        /// <summary>Straight pieces of a wall location curve (arcs are split into ~500 mm chords).</summary>
        private static IEnumerable<Tuple<XYZ, XYZ>> Flatten(Curve c)
        {
            if (c is Line)
            {
                yield return Tuple.Create(c.GetEndPoint(0), c.GetEndPoint(1));
                yield break;
            }
            var pts = c.Tessellate();
            // Tessellate is chord-tolerance based; resample long curves so every chord stays short
            var dense = new List<XYZ>();
            double len = c.Length;
            int n = Math.Max(2, (int)Math.Ceiling(len * FtToMm / 500));
            for (int i = 0; i <= n; i++) dense.Add(c.Evaluate((double)i / n, true));
            if (dense.Count < pts.Count) dense = pts.ToList();
            for (int i = 0; i + 1 < dense.Count; i++) yield return Tuple.Create(dense[i], dense[i + 1]);
        }

        #endregion

        #region openings

        private void ReadOpenings(Document doc, Transform t, string pre, RevitSnapshot snap)
        {
            var cats = new[] { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows };
            foreach (var bic in cats)
            {
                foreach (var fi in new FilteredElementCollector(doc).OfCategory(bic).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
                {
                    try
                    {
                        if (fi.Host is Wall hw && hw.WallType.Kind == WallKind.Curtain) continue; // curtain panels
                        XYZ p = (fi.Location as LocationPoint)?.Point;
                        if (p == null) continue;
                        double angle = 0;
                        string hostId = null;
                        if (fi.Host is Wall host && host.Location is LocationCurve hlc && hlc.Curve is Line hl)
                        {
                            // project onto the host's true centre line so it compares with the CAD opening centre
                            var cs = host.WallType.GetCompoundStructure();
                            double off = 0;
                            var key = host.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
                            if (cs != null && key != null) { try { off = cs.GetOffsetForLocationLine((WallLocationLine)key.AsInteger()); } catch { } }
                            var shift = CentreShift(host, hl, off);
                            var a = hl.GetEndPoint(0) + shift;
                            var b = hl.GetEndPoint(1) + shift;
                            var dir = (b - a).Normalize();
                            double tt = (p - a).DotProduct(dir);
                            p = new XYZ(a.X + dir.X * tt, a.Y + dir.Y * tt, p.Z);
                            angle = Math.Atan2(dir.Y, dir.X);
                            hostId = pre + Id(host.Id);
                        }
                        var sym = fi.Symbol;
                        double? width = Len(sym, bic == BuiltInCategory.OST_Doors ? BuiltInParameter.DOOR_WIDTH : BuiltInParameter.WINDOW_WIDTH)
                                        ?? Len(fi, bic == BuiltInCategory.OST_Doors ? BuiltInParameter.DOOR_WIDTH : BuiltInParameter.WINDOW_WIDTH)
                                        ?? Len(sym, BuiltInParameter.FAMILY_WIDTH_PARAM) ?? Len(fi, BuiltInParameter.FAMILY_WIDTH_PARAM)
                                        ?? LenByName(fi, "Width") ?? LenByName(sym, "Width")
                                        ?? Len(sym, BuiltInParameter.FAMILY_ROUGH_WIDTH_PARAM) ?? LenByName(sym, "Rough Width");
                        double? height = Len(sym, bic == BuiltInCategory.OST_Doors ? BuiltInParameter.DOOR_HEIGHT : BuiltInParameter.WINDOW_HEIGHT)
                                         ?? Len(sym, BuiltInParameter.FAMILY_HEIGHT_PARAM) ?? LenByName(sym, "Height");
                        var wp = t.OfPoint(p);
                        var wdir = t.OfVector(new XYZ(Math.Cos(angle), Math.Sin(angle), 0));
                        var levelId = fi.LevelId != ElementId.InvalidElementId ? fi.LevelId : fi.Host?.LevelId ?? ElementId.InvalidElementId;
                        snap.Openings.Add(new QcOpening
                        {
                            Id = pre + Id(fi.Id),
                            Kind = bic == BuiltInCategory.OST_Doors ? OpeningKind.Door : OpeningKind.Window,
                            Position = P(wp),
                            Width = Math.Round((width ?? 0) * FtToMm, 1),
                            Height = height.HasValue ? Math.Round(height.Value * FtToMm, 1) : (double?)null,
                            SillHeight = Len(fi, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM) is double sh ? Math.Round(sh * FtToMm, 1) : (double?)null,
                            Angle = GeoMath.NormalizeUndirected(Math.Atan2(wdir.Y, wdir.X)),
                            Mark = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                            TypeMark = sym.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.AsString(),
                            TypeName = sym.Name,
                            FamilyName = sym.FamilyName,
                            HostWallId = hostId,
                            LevelName = MapLevelName(doc, levelId)
                        });
                    }
                    catch (Exception ex) { Warnings.Add($"Opening {fi.Id}: {ex.Message}"); }
                }
            }
        }

        #endregion

        #region columns

        private void ReadColumns(Document doc, Transform t, string pre, RevitSnapshot snap)
        {
            var filter = new ElementMulticategoryFilter(new List<BuiltInCategory> { BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns });
            var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Coarse, IncludeNonVisibleObjects = false };
            foreach (var fi in new FilteredElementCollector(doc).WherePasses(filter).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
            {
                try
                {
                    var pts = new List<Vec2>();
                    CollectPlanPoints(fi.get_Geometry(opt), t, pts);
                    var rect = pts.Count >= 3 ? GeoMath.MinAreaRect(pts) : null;
                    var sym = fi.Symbol;
                    double? dia = LenByName(sym, "Diameter") ?? LenByName(fi, "Diameter") ?? LenByName(sym, "d") ?? LenByName(sym, "D");
                    bool round = dia.HasValue || (sym.FamilyName ?? "").IndexOf("round", StringComparison.OrdinalIgnoreCase) >= 0
                                 || (sym.FamilyName ?? "").IndexOf("circ", StringComparison.OrdinalIgnoreCase) >= 0;
                    Vec2 center;
                    double w, d, ang;
                    if (rect != null)
                    {
                        center = rect.Center; w = rect.Width; d = rect.Depth; ang = rect.Angle;
                    }
                    else
                    {
                        var lp = (fi.Location as LocationPoint)?.Point ?? (fi.Location as LocationCurve)?.Curve.GetEndPoint(0);
                        if (lp == null) continue;
                        center = P(t.OfPoint(lp));
                        w = ((LenByName(sym, "b") ?? LenByName(sym, "Width") ?? dia ?? 0) * FtToMm);
                        d = ((LenByName(sym, "h") ?? LenByName(sym, "Depth") ?? dia ?? 0) * FtToMm);
                        ang = (fi.Location as LocationPoint)?.Rotation ?? 0;
                    }
                    if (round) { double s = dia.HasValue ? dia.Value * FtToMm : Math.Max(w, d); w = d = s; }
                    var col = new QcColumn
                    {
                        Id = pre + Id(fi.Id),
                        Center = center,
                        Width = Math.Round(w, 1),
                        Depth = Math.Round(d, 1),
                        Angle = GeoMath.NormalizeUndirected(ang),
                        IsCircular = round,
                        IsStructural = fi.Category?.Id != null && Id(fi.Category.Id) == ((int)BuiltInCategory.OST_StructuralColumns).ToString(),
                        TypeName = $"{sym.FamilyName}: {sym.Name}",
                        LevelName = MapLevelName(doc, fi.LevelId),
                        Levels = LevelsCut(fi, t),
                        Mark = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString(),
                        Outline = rect?.Corners() ?? new List<Vec2>()
                    };
                    snap.Columns.Add(col);
                }
                catch (Exception ex) { Warnings.Add($"Column {fi.Id}: {ex.Message}"); }
            }
        }

        private static void CollectPlanPoints(GeometryElement ge, Transform t, List<Vec2> pts)
        {
            if (ge == null) return;
            foreach (var o in ge)
            {
                switch (o)
                {
                    case Solid s when s.Volume > 1e-9:
                        foreach (Edge e in s.Edges)
                            foreach (var p in e.Tessellate()) pts.Add(P(t.OfPoint(p)));
                        break;
                    case GeometryInstance gi:
                        CollectPlanPoints(gi.GetInstanceGeometry(), t, pts);
                        break;
                }
            }
        }

        #endregion

        #region rooms & grids

        private void ReadRooms(Document doc, Transform t, string pre, RevitSnapshot snap)
        {
            var opt = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
            foreach (var room in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>())
            {
                try
                {
                    var lp = (room.Location as LocationPoint)?.Point;
                    var r = new QcRoom
                    {
                        Id = pre + Id(room.Id),
                        Name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? room.Name,
                        Number = room.Number,
                        LevelName = MapLevelName(doc, room.LevelId),
                        IsPlaced = lp != null,
                        IsEnclosed = room.Area > 1e-6,
                        AreaM2 = Math.Round(room.Area * 0.09290304, 3)
                    };
                    if (lp != null) r.Position = P(t.OfPoint(lp));
                    if (r.IsPlaced && r.IsEnclosed)
                    {
                        var loops = room.GetBoundarySegments(opt);
                        List<Vec2> best = null;
                        double bestArea = 0;
                        foreach (var loop in loops ?? new List<IList<BoundarySegment>>())
                        {
                            var poly = new List<Vec2>();
                            foreach (var seg in loop)
                            {
                                var pts = seg.GetCurve().Tessellate();
                                for (int i = 0; i < pts.Count - 1; i++) poly.Add(P(t.OfPoint(pts[i])));
                            }
                            double a = Math.Abs(GeoMath.PolygonArea(poly));
                            if (a > bestArea) { bestArea = a; best = poly; }
                        }
                        if (best != null) r.Boundary = best;
                    }
                    snap.Rooms.Add(r);
                }
                catch (Exception ex) { Warnings.Add($"Room {room.Id}: {ex.Message}"); }
            }
        }

        private void ReadGrids(Document doc, RevitSnapshot snap)
        {
            foreach (var g in new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>())
            {
                if (!(g.Curve is Line l)) continue;
                snap.Grids.Add(new QcGrid { Id = Id(g.Id), Name = g.Name, Line = new Seg2(P(l.GetEndPoint(0)), P(l.GetEndPoint(1))) });
            }
        }

        #endregion

        #region helpers

        /// <summary>Host levels whose plan cut plane passes through the element.</summary>
        private List<string> LevelsCut(Element e, Transform t)
        {
            var res = new List<string>();
            var bb = e.get_BoundingBox(null);
            if (bb == null) return res;
            double z0 = t.OfPoint(bb.Min).Z, z1 = t.OfPoint(bb.Max).Z;
            double lo = Math.Min(z0, z1), hi = Math.Max(z0, z1);
            foreach (var (level, cutZ) in _levels)
                if (cutZ >= lo - 1e-6 && cutZ <= hi + 1e-6) res.Add(level.Name);
            return res;
        }

        /// <summary>Level name in the host document (linked elements are matched by elevation).</summary>
        private string MapLevelName(Document doc, ElementId levelId)
        {
            if (levelId == null || levelId == ElementId.InvalidElementId) return null;
            if (!(doc.GetElement(levelId) is Level l)) return null;
            if (doc.Equals(_doc)) return l.Name;
            var match = _levels.OrderBy(x => Math.Abs(x.level.ProjectElevation - l.ProjectElevation)).FirstOrDefault();
            return match.level?.Name ?? l.Name;
        }

        private static string LevelName(Document doc, ElementId id) => (doc.GetElement(id) as Level)?.Name;

        private static double? Len(Element e, BuiltInParameter bip)
        {
            var p = e?.get_Parameter(bip);
            return p != null && p.HasValue && p.StorageType == StorageType.Double && p.AsDouble() > 0 ? p.AsDouble() : (double?)null;
        }

        private static double? LenByName(Element e, string name)
        {
            var p = e?.LookupParameter(name);
            return p != null && p.HasValue && p.StorageType == StorageType.Double && p.AsDouble() > 0 ? p.AsDouble() : (double?)null;
        }

        private static Vec2 P(XYZ p) => new Vec2(p.X * FtToMm, p.Y * FtToMm);

#if REVIT2024 || REVIT2025 || REVIT2026 || REVIT2027
        public static string Id(ElementId id) => id.Value.ToString();
#else
        public static string Id(ElementId id) => id.IntegerValue.ToString();
#endif

        #endregion
    }
}
