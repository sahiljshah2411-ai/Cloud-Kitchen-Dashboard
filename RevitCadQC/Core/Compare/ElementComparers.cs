using System;
using System.Collections.Generic;
using System.Linq;
using RevitCadQC.Core.Extraction;
using RevitCadQC.Core.Geometry;
using RevitCadQC.Core.Model;
using RevitCadQC.Core.Report;
using static RevitCadQC.Core.Compare.FloorContext;

namespace RevitCadQC.Core.Compare
{
    public sealed class OpeningComparer
    {
        public void Compare(FloorContext ctx)
        {
            var s = ctx.Settings;
            var cad = ctx.Cad.Openings.Where(o => (o.Kind == OpeningKind.Door && s.CheckDoors) || (o.Kind != OpeningKind.Door && s.CheckWindows)).ToList();
            var rvt = ctx.RevitOpenings.Where(o => (o.Kind == OpeningKind.Door && s.CheckDoors) || (o.Kind != OpeningKind.Door && s.CheckWindows)).ToList();

            var pairs = new List<(double score, QcOpening c, QcOpening r)>();
            foreach (var c in cad)
                foreach (var r in rvt)
                {
                    double d = c.Position.DistanceTo(r.Position);
                    if (d > s.OpeningSearchRadius + Math.Max(c.Width, r.Width) / 2) continue;
                    double score = d + (c.Kind != r.Kind ? 400 : 0) + Math.Abs(c.Width - r.Width) * 0.3;
                    pairs.Add((score, c, r));
                }
            var usedC = new HashSet<QcOpening>();
            var usedR = new HashSet<QcOpening>();
            foreach (var p in pairs.OrderBy(p => p.score))
            {
                if (usedC.Contains(p.c) || usedR.Contains(p.r)) continue;
                usedC.Add(p.c); usedR.Add(p.r);
                CheckPair(ctx, p.c, p.r);
            }

            foreach (var c in cad.Where(c => !usedC.Contains(c)))
            {
                string cat = c.Kind == OpeningKind.Door ? IssueCategory.Door : IssueCategory.Window;
                string label = c.Kind == OpeningKind.Door ? "Door" : "Window";
                ctx.Add(cat, IssueType.MissingInRevit, Severity.Major,
                    $"{label}{(string.IsNullOrEmpty(c.Tag) ? "" : " " + c.Tag)} ({Mm(c.Width)}) missing in Revit", c.Position,
                    $"CAD {label.ToLowerInvariant()} (block '{c.BlockName ?? "-"}', layer '{c.Layer}', width from {c.WidthSource}) has no Revit {label.ToLowerInvariant()} within {Mm(s.OpeningSearchRadius)}.",
                    $"{c.Tag} {Mm(c.Width)}".Trim(), "—", c.Width, null, new[] { c.Handle }, c.Layer, null, Math.Max(450, c.Width * 0.7));
            }
            if (s.ReportExtraRevitElements && cad.Count > 0)
            {
                foreach (var r in rvt.Where(r => !usedR.Contains(r)))
                {
                    string cat = r.Kind == OpeningKind.Door ? IssueCategory.Door : IssueCategory.Window;
                    string label = r.Kind == OpeningKind.Door ? "Door" : "Window";
                    ctx.Add(cat, IssueType.ExtraInRevit, Severity.Major,
                        $"Revit {label.ToLowerInvariant()} {r.Mark ?? r.TypeMark ?? ""} ({Mm(r.Width)}) not in CAD".Replace("  ", " "), r.Position,
                        $"Revit {label.ToLowerInvariant()} {r.Id} '{r.FamilyName}: {r.TypeName}' has no matching opening in the CAD plan.",
                        "—", $"{r.TypeName} {Mm(r.Width)}", r.Width, new[] { r.Id, r.HostWallId }, null, null, null, Math.Max(450, r.Width * 0.7));
                }
            }
        }

        private static void CheckPair(FloorContext ctx, QcOpening c, QcOpening r)
        {
            var s = ctx.Settings;
            string cat = c.Kind == OpeningKind.Door ? IssueCategory.Door : IssueCategory.Window;
            string what = c.Kind == OpeningKind.Door ? "Door" : "Window";
            var ids = new[] { r.Id };
            if (c.Kind != r.Kind)
            {
                ctx.Add(cat, IssueType.KindMismatch, Severity.Major,
                    $"CAD shows a {c.Kind.ToString().ToLowerInvariant()}, Revit has a {r.Kind.ToString().ToLowerInvariant()}", r.Position,
                    $"CAD {c.Kind} (block '{c.BlockName}') vs Revit {r.Kind} '{r.FamilyName}: {r.TypeName}'.",
                    c.Kind.ToString(), r.Kind.ToString(), null, ids, new[] { c.Handle }, c.Layer);
            }
            double dw = r.Width - c.Width;
            if (Math.Abs(dw) > s.OpeningWidthTol)
            {
                ctx.Add(cat, IssueType.WidthMismatch, Math.Abs(dw) >= 100 ? Severity.Major : Severity.Minor,
                    $"{what} width: CAD {Mm(c.Width)} vs Revit {Mm(r.Width)}", r.Position,
                    $"{what} {c.Tag ?? r.Mark ?? ""}: CAD width {Mm(c.Width)} (from {c.WidthSource}), Revit '{r.TypeName}' width {Mm(r.Width)}.",
                    Mm(c.Width), $"{Mm(r.Width)} — {r.TypeName}", dw, ids, new[] { c.Handle }, c.Layer, null, Math.Max(450, c.Width * 0.7));
            }
            double d = c.Position.DistanceTo(r.Position);
            if (d > s.OpeningPositionTol)
            {
                ctx.Add(cat, IssueType.PositionOffset, d > 3 * s.OpeningPositionTol ? Severity.Major : Severity.Minor,
                    $"{what} {c.Tag ?? r.Mark ?? ""} shifted {Mm(d)}".Replace("  ", " "), r.Position,
                    $"Opening centre differs by {Mm1(d)} between CAD and Revit.",
                    null, null, d, ids, new[] { c.Handle }, c.Layer, new List<Vec2> { c.Position, r.Position }, Math.Max(450, c.Width * 0.7));
            }
            if (!string.IsNullOrWhiteSpace(c.Tag) && (!string.IsNullOrWhiteSpace(r.Mark) || !string.IsNullOrWhiteSpace(r.TypeMark)))
            {
                var t = TextParsing.NormalizeTag(c.Tag);
                if (t != TextParsing.NormalizeTag(r.TypeMark) && t != TextParsing.NormalizeTag(r.Mark))
                {
                    ctx.Add(cat, IssueType.MarkMismatch, Severity.Minor,
                        $"{what} tag: CAD {c.Tag} vs Revit {r.TypeMark ?? r.Mark}", r.Position,
                        $"CAD tag '{c.Tag}' does not match Revit Type Mark '{r.TypeMark}' / Mark '{r.Mark}'.",
                        c.Tag, $"Type Mark {r.TypeMark} / Mark {r.Mark}", null, ids, new[] { c.Handle }, c.Layer);
                }
            }
        }
    }

    public sealed class ColumnComparer
    {
        public void Compare(FloorContext ctx)
        {
            var s = ctx.Settings;
            var cad = ctx.Cad.Columns;
            var rvt = ctx.RevitColumns;
            var pairs = new List<(double d, QcColumn c, QcColumn r)>();
            foreach (var c in cad)
                foreach (var r in rvt)
                {
                    double d = c.Center.DistanceTo(r.Center);
                    if (d <= s.ColumnSearchRadius + Math.Max(c.MaxSize, r.MaxSize) / 2) pairs.Add((d, c, r));
                }
            var uc = new HashSet<QcColumn>(); var ur = new HashSet<QcColumn>();
            foreach (var p in pairs.OrderBy(p => p.d))
            {
                if (uc.Contains(p.c) || ur.Contains(p.r)) continue;
                uc.Add(p.c); ur.Add(p.r);
                Check(ctx, p.c, p.r, p.d);
            }
            foreach (var c in cad.Where(c => !uc.Contains(c)))
                ctx.Add(IssueCategory.Column, IssueType.MissingInRevit, Severity.Critical,
                    $"Column {Size(c)} missing in Revit", c.Center,
                    $"CAD column on layer '{c.Layer}' ({Size(c)}) has no Revit column within {Mm(s.ColumnSearchRadius)}.",
                    Size(c), "—", null, null, new[] { c.Handle }, c.Layer, c.Outline.Count > 0 ? c.Outline.Concat(new[] { c.Outline[0] }).ToList() : null, Math.Max(400, c.MaxSize));
            if (s.ReportExtraRevitElements && cad.Count > 0)
                foreach (var r in rvt.Where(r => !ur.Contains(r)))
                    ctx.Add(IssueCategory.Column, IssueType.ExtraInRevit, Severity.Major,
                        $"Revit column {Size(r)} not in CAD", r.Center,
                        $"Revit column {r.Id} '{r.TypeName}' has no matching column in the CAD plan.",
                        "—", $"{r.TypeName} {Size(r)}", null, new[] { r.Id }, markerRadius: Math.Max(400, r.MaxSize));
        }

        private static string Size(QcColumn c) => c.IsCircular ? $"Ø{c.Width:0}" : $"{c.Width:0}×{c.Depth:0}";

        private static void Check(FloorContext ctx, QcColumn c, QcColumn r, double d)
        {
            var s = ctx.Settings;
            var ids = new[] { r.Id };
            bool sizeBad = c.IsCircular != r.IsCircular ||
                           Math.Abs(c.MinSize - r.MinSize) > s.ColumnSizeTol || Math.Abs(c.MaxSize - r.MaxSize) > s.ColumnSizeTol;
            if (sizeBad)
                ctx.Add(IssueCategory.Column, IssueType.SizeMismatch, Severity.Major,
                    $"Column size: CAD {Size(c)} vs Revit {Size(r)}", r.Center,
                    $"Revit column '{r.TypeName}' is {Size(r)}; the CAD column is {Size(c)}.",
                    Size(c), $"{Size(r)} — {r.TypeName}", r.Width * r.Depth - c.Width * c.Depth, ids, new[] { c.Handle }, c.Layer, null, Math.Max(400, c.MaxSize));
            if (d > s.ColumnPositionTol)
                ctx.Add(IssueCategory.Column, IssueType.PositionOffset, d > 50 ? Severity.Major : Severity.Minor,
                    $"Column shifted {Mm(d)}", r.Center,
                    $"Column centre differs by {Mm1(d)} between CAD and Revit.",
                    null, null, d, ids, new[] { c.Handle }, c.Layer, new List<Vec2> { c.Center, r.Center }, Math.Max(400, c.MaxSize));
            if (!sizeBad && !c.IsCircular && !r.IsCircular && c.MaxSize - c.MinSize > s.ColumnSizeTol)
            {
                double ca = c.Width >= c.Depth ? c.Angle : c.Angle + Math.PI / 2;
                double ra = r.Width >= r.Depth ? r.Angle : r.Angle + Math.PI / 2;
                double da = GeoMath.UndirectedDiff(ca, ra);
                if (da > s.ColumnRotationTolDeg * GeoMath.Deg)
                    ctx.Add(IssueCategory.Column, IssueType.RotationMismatch, Severity.Major,
                        $"Column {Size(c)} rotated {da / GeoMath.Deg:0.#}° vs CAD", r.Center,
                        "The long side of the Revit column points in a different direction from the CAD column.",
                        $"{ca / GeoMath.Deg:0.#}°", $"{ra / GeoMath.Deg:0.#}°", da / GeoMath.Deg, ids, new[] { c.Handle }, c.Layer, null, Math.Max(400, c.MaxSize));
            }
        }
    }

    public sealed class GridComparer
    {
        public void Compare(FloorContext ctx)
        {
            var s = ctx.Settings;
            var cad = ctx.Cad.Grids.Where(g => !string.IsNullOrEmpty(g.Name)).ToList();
            if (cad.Count == 0) return;
            foreach (var c in cad)
            {
                var r = ctx.RevitGrids.FirstOrDefault(g => Norm(g.Name) == Norm(c.Name));
                if (r == null)
                {
                    ctx.Add(IssueCategory.Grid, IssueType.MissingInRevit, Severity.Major, $"Grid {c.Name} missing in Revit", c.Line.Mid,
                        $"CAD grid '{c.Name}' has no Revit grid with the same name.", c.Name, "—", null, null, new[] { c.Handle }, c.Layer,
                        new List<Vec2> { c.Line.A, c.Line.B }, 800);
                    continue;
                }
                double da = GeoMath.UndirectedDiff(c.Line.Angle, r.Line.Angle);
                double off = Math.Abs(r.Line.SignedOffset(c.Line.Mid));
                if (da > s.GridAngleTolDeg * GeoMath.Deg)
                    ctx.Add(IssueCategory.Grid, IssueType.RotationMismatch, Severity.Major, $"Grid {c.Name} angle differs by {da / GeoMath.Deg:0.###}°", c.Line.Mid,
                        "The Revit grid is not parallel to the CAD grid.", null, null, da / GeoMath.Deg, new[] { r.Id }, new[] { c.Handle }, c.Layer,
                        new List<Vec2> { c.Line.A, c.Line.B }, 800);
                else if (off > s.GridOffsetTol)
                    ctx.Add(IssueCategory.Grid, IssueType.PositionOffset, off > 25 ? Severity.Critical : Severity.Major, $"Grid {c.Name} offset {Mm1(off)}", c.Line.Mid,
                        $"Revit grid '{r.Name}' is {Mm1(off)} from the CAD grid line (all other checks are aligned to the grids, so this is relative to the rest of the grid system).",
                        null, null, off, new[] { r.Id }, new[] { c.Handle }, c.Layer, new List<Vec2> { c.Line.A, c.Line.B }, 800);
            }
            if (s.ReportExtraRevitElements)
            {
                var cadNames = new HashSet<string>(cad.Select(g => Norm(g.Name)));
                var box = ctx.Cad.Bounds.IsEmpty ? (Box2?)null : ctx.Cad.Bounds;
                foreach (var r in ctx.RevitGrids.Where(g => !cadNames.Contains(Norm(g.Name))))
                    ctx.Add(IssueCategory.Grid, IssueType.ExtraInRevit, Severity.Minor, $"Revit grid {r.Name} not in CAD", r.Line.Mid,
                        $"Revit grid '{r.Name}' has no CAD grid with the same name on this plan.", "—", r.Name, null, new[] { r.Id }, markerRadius: 800);
            }
        }

        private static string Norm(string s) => (s ?? "").Trim().Replace(" ", "").ToUpperInvariant();
    }

    public sealed class RoomComparer
    {
        public void Compare(FloorContext ctx)
        {
            var s = ctx.Settings;
            var cad = ctx.Cad.Rooms;
            var rvt = ctx.RevitRooms;

            foreach (var r in rvt.Where(r => !r.IsPlaced || !r.IsEnclosed))
                ctx.Add(IssueCategory.ModelHygiene, IssueType.NotEnclosed, Severity.Major,
                    $"Room '{r.Name}' {(r.IsPlaced ? "not enclosed" : "not placed")}", r.IsPlaced ? r.Position : (Vec2?)null,
                    "The Revit room is redundant, unplaced or not enclosed by room-bounding elements; area and boundary checks are unreliable.",
                    null, r.Name, null, new[] { r.Id }, markerRadius: 800);
            if (cad.Count == 0) return;

            var placed = rvt.Where(r => r.IsPlaced && r.IsEnclosed && r.Boundary.Count >= 3).ToList();
            var hits = new Dictionary<QcRoom, List<QcRoom>>();
            foreach (var c in cad)
            {
                var host = placed.FirstOrDefault(r => GeoMath.PointInPolygon(c.Position, r.Boundary));
                if (host == null)
                {
                    var near = placed.Where(r => TextParsing.RoomNamesEquivalent(r.Name, c.Name))
                                     .OrderBy(r => r.Position.DistanceTo(c.Position)).FirstOrDefault(r => r.Position.DistanceTo(c.Position) < 1500);
                    if (near != null) host = near;
                }
                if (host == null)
                {
                    ctx.Add(IssueCategory.Room, IssueType.MissingInRevit, Severity.Major, $"Room '{c.Name}' has no Revit room", c.Position,
                        "There is no placed, enclosed Revit room at the CAD room label.", c.Name, "—", null, null, new[] { c.Handle }, c.Layer, null, 900);
                    continue;
                }
                if (!hits.TryGetValue(host, out var l)) hits[host] = l = new List<QcRoom>();
                l.Add(c);

                if (!TextParsing.RoomNamesEquivalent(c.Name, host.Name))
                    ctx.Add(IssueCategory.Room, IssueType.NameMismatch, Severity.Minor, $"Room name: CAD '{c.Name}' vs Revit '{host.Name}'", c.Position,
                        $"Revit room {host.Number} is named '{host.Name}'.", c.Name, $"{host.Number} {host.Name}".Trim(), null, new[] { host.Id }, new[] { c.Handle }, c.Layer, null, 900);

                if (c.SizeA.HasValue && c.SizeB.HasValue)
                {
                    var rect = GeoMath.MinAreaRect(host.Boundary);
                    if (rect != null)
                    {
                        double ca = Math.Min(c.SizeA.Value, c.SizeB.Value), cb = Math.Max(c.SizeA.Value, c.SizeB.Value);
                        double ra = rect.Min, rb = rect.Max;
                        double fill = Math.Abs(GeoMath.PolygonArea(host.Boundary)) / Math.Max(1, rect.Width * rect.Depth);
                        if (fill > 0.9 && (Math.Abs(ca - ra) > s.RoomSizeTol || Math.Abs(cb - rb) > s.RoomSizeTol))
                            ctx.Add(IssueCategory.Room, IssueType.SizeMismatch, Severity.Major,
                                $"Room '{c.Name}' size: CAD {ca:0}×{cb:0} vs Revit {ra:0}×{rb:0}", c.Position,
                                $"CAD size note \"{c.SizeText}\" vs the Revit room boundary (clear internal dimensions).",
                                $"{ca:0} × {cb:0}", $"{ra:0} × {rb:0}", Math.Max(Math.Abs(ca - ra), Math.Abs(cb - rb)),
                                new[] { host.Id }, new[] { c.Handle }, c.Layer, host.Boundary.Concat(new[] { host.Boundary[0] }).ToList(), 900);
                    }
                }
                if (c.AreaM2.HasValue && host.AreaM2.HasValue && host.AreaM2 > 0)
                {
                    double pct = Math.Abs(host.AreaM2.Value - c.AreaM2.Value) / c.AreaM2.Value * 100;
                    if (pct > s.RoomAreaTolPercent)
                        ctx.Add(IssueCategory.Room, IssueType.AreaMismatch, Severity.Minor,
                            $"Room '{c.Name}' area: CAD {c.AreaM2:0.00} m² vs Revit {host.AreaM2:0.00} m²", c.Position,
                            $"Area differs by {pct:0.0} %.", $"{c.AreaM2:0.00} m²", $"{host.AreaM2:0.00} m²", host.AreaM2 - c.AreaM2, new[] { host.Id }, new[] { c.Handle }, c.Layer, null, 900);
                }
            }

            // two different CAD rooms inside one Revit room → a separating wall is probably missing
            foreach (var kv in hits.Where(h => h.Value.Count > 1))
            {
                var distinct = new List<QcRoom>();
                foreach (var c in kv.Value)
                    if (!distinct.Any(d => TextParsing.RoomNamesEquivalent(d.Name, c.Name) && d.Position.DistanceTo(c.Position) < 1500)) distinct.Add(c);
                if (distinct.Count < 2) continue;
                ctx.Add(IssueCategory.Room, IssueType.Duplicate, Severity.Major,
                    $"CAD rooms {string.Join(" + ", distinct.Select(d => "'" + d.Name + "'"))} are one Revit room", kv.Key.Position,
                    $"Revit room '{kv.Key.Name}' contains {distinct.Count} separate CAD rooms. A separating wall or room separation line is probably missing.",
                    string.Join(", ", distinct.Select(d => d.Name)), kv.Key.Name, distinct.Count, new[] { kv.Key.Id }, distinct.Select(d => d.Handle), null,
                    kv.Key.Boundary.Concat(new[] { kv.Key.Boundary[0] }).ToList(), 1200);
            }

            if (s.ReportExtraRevitElements)
                foreach (var r in placed.Where(r => !hits.ContainsKey(r)))
                    ctx.Add(IssueCategory.Room, IssueType.ExtraInRevit, Severity.Info, $"Revit room '{r.Name}' has no CAD label", r.Position,
                        "No CAD room name lies inside this Revit room.", "—", $"{r.Number} {r.Name}".Trim(), null, new[] { r.Id }, markerRadius: 900);
        }
    }

    /// <summary>
    /// Checks every CAD linear dimension that runs between wall faces or grid lines against the same two
    /// references in Revit ("CAD says 3000 clear, Revit measures 2950"). Also flags CAD dimensions whose text was overridden.
    /// </summary>
    public sealed class DimensionComparer
    {
        private sealed class Ref
        {
            public Seg2 Line;
            public string What;
            public Func<Seg2> RevitLine;
            public string RevitId;
        }

        public void Compare(FloorContext ctx)
        {
            var s = ctx.Settings;
            var refs = new List<Ref>();
            foreach (var w in ctx.Cad.Walls)
            {
                ctx.WallMatches.TryGetValue(w.Id, out var rw);
                var n = w.Centerline.Dir.Perp;
                foreach (int side in new[] { 1, -1 })
                {
                    var face = new Seg2(w.Centerline.A + n * (side * w.Thickness / 2), w.Centerline.B + n * (side * w.Thickness / 2));
                    Func<Seg2> rf = null;
                    if (rw != null)
                    {
                        // same physical side: pick the Revit face whose normal points the same way as the CAD face
                        var rn = rw.Centerline.Dir.Perp;
                        int rs = rn.Dot(n) >= 0 ? side : -side;
                        rf = () => new Seg2(rw.Centerline.A + rn * (rs * rw.Thickness / 2), rw.Centerline.B + rn * (rs * rw.Thickness / 2));
                    }
                    refs.Add(new Ref { Line = face, What = "wall face", RevitLine = rf, RevitId = rw?.Id });
                }
            }
            foreach (var g in ctx.Cad.Grids.Where(g => g.Name != null))
            {
                var rg = ctx.RevitGrids.FirstOrDefault(x => string.Equals(x.Name?.Trim(), g.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                refs.Add(new Ref { Line = g.Line, What = "grid " + g.Name, RevitLine = rg == null ? (Func<Seg2>)null : () => rg.Line, RevitId = rg?.Id });
            }

            int reported = 0;
            foreach (var d in ctx.Cad.Dimensions)
            {
                if (s.CheckCadDrafting && d.IsOverridden && Math.Abs(d.ValueMm - d.GeometricMm) > s.DimensionTol)
                    ctx.Add(IssueCategory.CadDrafting, IssueType.DimensionOverride, Severity.Major,
                        $"Dimension text \"{d.Text}\" but drawn {Mm(d.GeometricMm)}", d.TextPosition,
                        "The dimension text was typed over and does not match the drawn geometry. The model may follow either value.",
                        d.Text, null, d.ValueMm - d.GeometricMm, null, new[] { d.Handle }, d.Layer, new List<Vec2> { d.P1, d.P2 }, 500);

                var r1 = Snap(refs, d.P1, d.Direction, 5);
                var r2 = Snap(refs, d.P2, d.Direction, 5);
                if (r1 == null || r2 == null || r1 == r2 || r1.RevitLine == null || r2.RevitLine == null) continue;
                var l1 = r1.RevitLine(); var l2 = r2.RevitLine();
                // measure between the two Revit references along the dimension direction, at the dimension location
                var foot1 = l1.PointAt(l1.Project(d.P1));
                var foot2 = l2.PointAt(l2.Project(d.P2));
                double revit = Math.Abs((foot2 - foot1).Dot(d.Direction));
                double cadValue = d.ValueMm;
                double diff = revit - cadValue;
                if (Math.Abs(diff) <= s.DimensionTol) continue;
                if (++reported > 400) break;
                ctx.Add(IssueCategory.Dimension, IssueType.DimensionMismatch, Math.Abs(diff) >= 50 ? Severity.Major : Severity.Minor,
                    $"Dimension {Mm(cadValue)} in CAD, {Mm(revit)} in Revit", (d.P1 + d.P2) * 0.5,
                    $"CAD dimension between {r1.What} and {r2.What} reads {Mm(cadValue)}; the same references in Revit are {Mm1(revit)} apart.",
                    Mm(cadValue), Mm1(revit), diff, new[] { r1.RevitId, r2.RevitId }, new[] { d.Handle }, d.Layer, new List<Vec2> { d.P1, d.P2 }, 450);
            }
        }

        private static Ref Snap(List<Ref> refs, Vec2 p, Vec2 dir, double tol)
        {
            Ref best = null;
            double bd = tol;
            foreach (var r in refs)
            {
                // the reference must be (nearly) perpendicular to the measuring direction
                if (Math.Abs(r.Line.Dir.Dot(dir)) > 0.02) continue;
                double t = r.Line.Project(p);
                if (t < -50 || t > r.Line.Length + 50) continue;
                double d = Math.Abs(r.Line.SignedOffset(p));
                if (d <= bd) { bd = d; best = r; }
            }
            return best;
        }
    }
}
