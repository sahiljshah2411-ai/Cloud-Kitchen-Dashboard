using System;
using System.Collections.Generic;
using System.Linq;
using CadQC.Core.Extraction;
using CadQC.Core.Geometry;
using CadQC.Core.Model;
using CadQC.Core.Report;
using static CadQC.Core.Compare.FloorContext;

namespace CadQC.Core.Compare
{
    /// <summary>
    /// Wall-by-wall comparison. Each CAD wall is covered, interval by interval, by the nearest parallel Revit
    /// walls; the matched pairs are checked for thickness and position, uncovered CAD length becomes
    /// "missing in Revit" and uncovered Revit length (excluding openings and junctions) becomes "extra in Revit".
    /// </summary>
    public sealed class WallComparer
    {
        private sealed class Match
        {
            public QcWall Cad;
            public QcWall Revit;
            public double Lo, Hi;        // along the CAD wall
            public double SignedOffset;  // Revit centre line relative to CAD centre line (left positive)
            public double Length => Hi - Lo;
        }

        public void Compare(FloorContext ctx)
        {
            var s = ctx.Settings;
            var cadWalls = ctx.Cad.Walls;
            var rvtAll = ctx.RevitWalls.Where(w => w.Centerline != null && w.Centerline.Length > 1).ToList();
            var rvt = rvtAll.Where(w => !w.IsCurtain).ToList();
            var curtain = rvtAll.Where(w => w.IsCurtain).ToList();
            double angTol = 2 * GeoMath.Deg;

            var matches = new List<Match>();
            foreach (var c in cadWalls)
            {
                var cl = c.Centerline;
                double L = cl.Length;
                var cands = new List<(QcWall r, double lo, double hi, double off)>();
                foreach (var r in rvt)
                {
                    if (GeoMath.UndirectedDiff(cl.Angle, r.Centerline.Angle) > angTol) continue;
                    double t1 = cl.Project(r.Centerline.A), t2 = cl.Project(r.Centerline.B);
                    double lo = Math.Max(0, Math.Min(t1, t2)), hi = Math.Min(L, Math.Max(t1, t2));
                    if (hi - lo < Math.Min(80, L * 0.3)) continue;
                    double mid = (lo + hi) / 2;
                    // offset measured at the middle of the overlap (robust to tiny angle differences)
                    var rMid = r.Centerline.PointAt(GeoMath.Clamp(r.Centerline.Project(cl.PointAt(mid)), 0, r.Centerline.Length));
                    double off = cl.SignedOffset(rMid);
                    double limit = Math.Max(s.WallMatchSearch, (c.Thickness + r.Thickness) / 2);
                    if (Math.Abs(off) > limit) continue;
                    cands.Add((r, lo, hi, off));
                }
                var used = new List<Interval>();
                foreach (var cd in cands.OrderBy(x => Math.Abs(x.off) + ThicknessPenalty(c, x.r)))
                {
                    foreach (var free in GeoMath.Uncovered(cd.lo, cd.hi, used))
                    {
                        if (free.Length < Math.Min(80, L * 0.3)) continue;
                        used.Add(free);
                        matches.Add(new Match { Cad = c, Revit = cd.r, Lo = free.Lo, Hi = free.Hi, SignedOffset = cd.off });
                    }
                }
            }

            // ---- per CAD wall: coverage, thickness, position ----
            foreach (var c in cadWalls)
            {
                var ms = matches.Where(m => m.Cad == c).ToList();
                var cl = c.Centerline;
                if (ms.Count > 0) ctx.WallMatches[c.Id] = ms.OrderByDescending(m => m.Length).First().Revit;

                // coverage
                double minLen = Math.Max(s.MinIssueLength, 0);
                foreach (var gap in GeoMath.Uncovered(0, cl.Length, ms.Select(m => new Interval(m.Lo, m.Hi))))
                {
                    if (gap.Length < minLen) continue;
                    var a = cl.PointAt(gap.Lo); var b = cl.PointAt(gap.Hi);
                    var mid = (a + b) * 0.5;
                    bool whole = gap.Length >= cl.Length - 1;
                    var curtainHit = curtain.FirstOrDefault(cw => GeoMath.UndirectedDiff(cw.Centerline.Angle, cl.Angle) < angTol && cw.Centerline.DistanceToPoint(mid) < c.Thickness + 150);
                    if (curtainHit != null)
                    {
                        ctx.Add(IssueCategory.Wall, IssueType.KindMismatch, Severity.Minor,
                            $"CAD wall ({Mm(c.Thickness)}) modelled as curtain wall in Revit", mid,
                            $"CAD shows a {Mm(c.Thickness)} wall over {Mm(gap.Length)}; Revit has curtain wall '{curtainHit.TypeName}' here.",
                            Mm(c.Thickness) + " wall", "Curtain wall " + curtainHit.TypeName, null,
                            new[] { curtainHit.Id }, c.Handles, c.Layer, new List<Vec2> { a, b });
                        continue;
                    }
                    string thk = Mm(c.AnnotatedThickness ?? c.Thickness);
                    ctx.Add(IssueCategory.Wall, whole ? IssueType.MissingInRevit : IssueType.PartiallyMissing,
                        whole ? Severity.Critical : Severity.Major,
                        whole ? $"Wall {thk} thick, {Mm(gap.Length)} long not modelled in Revit"
                              : $"{Mm(gap.Length)} of a {thk} wall not modelled in Revit",
                        mid,
                        whole ? $"The CAD wall on layer '{c.Layer}' ({thk} thick, {Mm(cl.Length)} long) has no Revit wall within {Mm(s.WallMatchSearch)}."
                              : $"The CAD wall on layer '{c.Layer}' is only partly modelled: {Mm(gap.Length)} of {Mm(cl.Length)} has no Revit wall.",
                        $"{thk} × {Mm(gap.Length)}", "—", gap.Length, null, c.Handles, c.Layer,
                        new List<Vec2> { a, b }, Math.Max(400, Math.Min(gap.Length / 2, 1500)));
                }

                // thickness & position per matched Revit wall
                foreach (var g in ms.GroupBy(m => m.Revit))
                {
                    var r = g.Key;
                    double len = g.Sum(m => m.Length);
                    double lo = g.Min(m => m.Lo), hi = g.Max(m => m.Hi);
                    var mid = cl.PointAt((lo + hi) / 2);
                    double off = g.OrderByDescending(m => m.Length).First().SignedOffset;
                    var path = new List<Vec2> { cl.PointAt(lo), cl.PointAt(hi) };

                    bool thkOk = ThicknessOk(ctx, c.Thickness, r, out string rvtThkText);
                    bool noteOk = c.AnnotatedThickness == null || ThicknessOk(ctx, c.AnnotatedThickness.Value, r, out _);
                    if (!thkOk || !noteOk)
                    {
                        double cadThk = !noteOk ? c.AnnotatedThickness.Value : c.Thickness;
                        string cadText = c.AnnotatedThickness.HasValue
                            ? $"{Mm(c.Thickness)} drawn, note \"{c.AnnotationText}\""
                            : $"{Mm(c.Thickness)} drawn";
                        ctx.Add(IssueCategory.Wall, IssueType.ThicknessMismatch,
                            Math.Abs(cadThk - r.Thickness) >= 50 ? Severity.Critical : Severity.Major,
                            $"Wall thickness: CAD {Mm(cadThk)} vs Revit {Mm(r.Thickness)}", mid,
                            $"CAD wall ({cadText}) is modelled with Revit type '{r.TypeName}' ({rvtThkText}) over {Mm(len)}.",
                            cadText, $"{rvtThkText} — {r.TypeName}", r.Thickness - cadThk,
                            new[] { r.Id }, c.Handles, c.Layer, path, 500);
                    }

                    // position: compare faces as well as centre lines
                    double cadHalf = c.Thickness / 2, rHalf = r.Thickness / 2;
                    double faceL = (off + rHalf) - cadHalf;   // left faces
                    double faceR = (off - rHalf) + cadHalf;   // right faces
                    double centre = Math.Abs(off);
                    double bestFace = Math.Min(Math.Abs(faceL), Math.Abs(faceR));
                    bool explainedByThickness = !thkOk && bestFace <= s.WallPositionTolMinor;
                    if (centre > s.WallPositionTolMinor && !explainedByThickness)
                    {
                        var sev = centre > s.WallPositionTolMajor ? Severity.Major : Severity.Minor;
                        ctx.Add(IssueCategory.Wall, IssueType.PositionOffset, sev,
                            $"Wall shifted {Mm(centre)} from CAD position", mid,
                            $"Revit wall '{r.TypeName}' centre line is {Mm1(centre)} off the CAD wall centre line " +
                            $"(faces differ by {Mm1(Math.Abs(faceL))} / {Mm1(Math.Abs(faceR))}).",
                            "centre line", $"offset {Mm1(centre)}", centre, new[] { r.Id }, c.Handles, c.Layer, path, 500);
                    }
                }

                // CAD-internal consistency: note vs drawn thickness
                if (s.CheckCadDrafting && c.AnnotatedThickness.HasValue && Math.Abs(c.AnnotatedThickness.Value - c.Thickness) > s.WallThicknessTol)
                {
                    ctx.Add(IssueCategory.CadDrafting, IssueType.AnnotationConflict, Severity.Minor,
                        $"CAD note \"{c.AnnotationText}\" but wall drawn {Mm(c.Thickness)}", cl.Mid,
                        "The thickness written on the drawing does not match the double-line spacing. Confirm the design thickness.",
                        c.AnnotationText, null, c.Thickness - c.AnnotatedThickness.Value, null, c.Handles, c.Layer,
                        new List<Vec2> { cl.A, cl.B }, 400);
                }
            }

            // ---- extra Revit walls ----
            if (s.ReportExtraRevitElements && cadWalls.Count > 0)
            {
                foreach (var r in rvt)
                {
                    var rl = r.Centerline;
                    var cover = new List<Interval>();
                    foreach (var m in matches.Where(m => m.Revit == r))
                    {
                        double a = rl.Project(m.Cad.Centerline.PointAt(m.Lo)), b = rl.Project(m.Cad.Centerline.PointAt(m.Hi));
                        cover.Add(new Interval(Math.Min(a, b), Math.Max(a, b)));
                    }
                    // openings in CAD sit in wall gaps; Revit walls run through them
                    foreach (var o in ctx.Cad.Openings)
                    {
                        if (Math.Abs(rl.SignedOffset(o.Position)) > r.Thickness / 2 + 150) continue;
                        if (GeoMath.UndirectedDiff(o.Angle, rl.Angle) > 10 * GeoMath.Deg) continue;
                        double t = rl.Project(o.Position);
                        cover.Add(new Interval(t - o.Width / 2 - 60, t + o.Width / 2 + 60));
                    }
                    // junctions: CAD walls crossing or ending on this line
                    foreach (var c in cadWalls)
                    {
                        if (GeoMath.UndirectedDiff(c.Centerline.Angle, rl.Angle) < 20 * GeoMath.Deg) continue;
                        foreach (var end in new[] { c.Centerline.A, c.Centerline.B })
                        {
                            if (Math.Abs(rl.SignedOffset(end)) > r.Thickness / 2 + c.Thickness + 50) continue;
                            double t = rl.Project(end);
                            cover.Add(new Interval(t - c.Thickness / 2 - 30, t + c.Thickness / 2 + 30));
                        }
                        if (AutoAligner_Intersect(rl, c.Centerline, out var ip) && c.Centerline.DistanceToPoint(ip) < 5)
                        {
                            double t = rl.Project(ip);
                            cover.Add(new Interval(t - c.Thickness / 2 - 30, t + c.Thickness / 2 + 30));
                        }
                    }
                    // Revit location lines run to the centre of the joined wall: tolerate up to half a wall at each end
                    double endTol = Math.Max(s.MinIssueLength, r.Thickness);
                    foreach (var gap in GeoMath.Uncovered(0, rl.Length, cover))
                    {
                        bool atEnd = gap.Lo <= 1 || gap.Hi >= rl.Length - 1;
                        if (gap.Length < (atEnd ? endTol : s.MinIssueLength)) continue;
                        bool whole = gap.Length >= rl.Length - 1;
                        var a = rl.PointAt(gap.Lo); var b = rl.PointAt(gap.Hi);
                        ctx.Add(IssueCategory.Wall, IssueType.ExtraInRevit, whole ? Severity.Major : Severity.Minor,
                            whole ? $"Revit wall '{r.TypeName}' ({Mm(gap.Length)}) not in CAD" : $"Revit wall extends {Mm(gap.Length)} beyond CAD",
                            (a + b) * 0.5,
                            whole ? $"Revit wall {r.Id} of type '{r.TypeName}' has no matching wall in the CAD plan."
                                  : $"{Mm(gap.Length)} of Revit wall {r.Id} ('{r.TypeName}') has no wall in the CAD plan (check wall ends / joins).",
                            "—", $"{r.TypeName}, {Mm(gap.Length)}", gap.Length, new[] { r.Id }, null, null,
                            new List<Vec2> { a, b }, Math.Max(400, Math.Min(gap.Length / 2, 1500)));
                    }
                }
            }

            // ---- Revit-only hygiene ----
            if (s.CheckWallTypeNames)
            {
                foreach (var r in rvt)
                {
                    var n = TextParsing.NumberInTypeName(r.TypeName);
                    if (n == null || n < 50 || n > 800) continue;
                    if (Math.Abs(n.Value - r.Thickness) <= 1 || (r.CoreThickness.HasValue && Math.Abs(n.Value - r.CoreThickness.Value) <= 1)) continue;
                    ctx.Add(IssueCategory.ModelHygiene, IssueType.TypeNameMismatch, Severity.Minor,
                        $"Type '{r.TypeName}' is actually {Mm(r.Thickness)} wide", r.Centerline.Mid,
                        $"The wall type name suggests {Mm(n.Value)} but the type width is {Mm(r.Thickness)}" +
                        (r.CoreThickness.HasValue ? $" (core {Mm(r.CoreThickness.Value)})." : "."),
                        null, $"{r.TypeName}: {Mm(r.Thickness)}", r.Thickness - n.Value, new[] { r.Id }, markerRadius: 400);
                }
            }
            if (s.CheckRevitDuplicates)
            {
                for (int i = 0; i < rvt.Count; i++)
                    for (int j = i + 1; j < rvt.Count; j++)
                    {
                        var a = rvt[i].Centerline; var b = rvt[j].Centerline;
                        if (GeoMath.UndirectedDiff(a.Angle, b.Angle) > angTol) continue;
                        if (Math.Abs(a.SignedOffset(b.Mid)) > Math.Min(rvt[i].Thickness, rvt[j].Thickness) / 2) continue;
                        double t1 = a.Project(b.A), t2 = a.Project(b.B);
                        double ov = Math.Min(a.Length, Math.Max(t1, t2)) - Math.Max(0, Math.Min(t1, t2));
                        if (ov < 200) continue;
                        var p = a.PointAt((Math.Max(0, Math.Min(t1, t2)) + Math.Min(a.Length, Math.Max(t1, t2))) / 2);
                        ctx.Add(IssueCategory.ModelHygiene, IssueType.Duplicate, Severity.Major,
                            $"Overlapping Revit walls ({Mm(ov)} overlap)", p,
                            $"Walls {rvt[i].Id} ('{rvt[i].TypeName}') and {rvt[j].Id} ('{rvt[j].TypeName}') overlap over {Mm(ov)}.",
                            null, "2 walls", ov, new[] { rvt[i].Id, rvt[j].Id }, markerRadius: 500);
                    }
            }

            // ---- CAD drafting: loose single lines on wall layers ----
            if (s.CheckCadDrafting)
            {
                foreach (var seg in ctx.Cad.UnpairedWallLines.Where(x => x.Length >= 1000).OrderByDescending(x => x.Length).Take(40))
                {
                    ctx.Add(IssueCategory.CadDrafting, IssueType.UnpairedLine, Severity.Info,
                        $"Single {Mm(seg.Length)} line on wall layer (no parallel face)", seg.Mid,
                        "A line on a wall layer has no parallel partner within the wall thickness range, so no thickness could be read. " +
                        "It may be a single-line wall, a sill/parapet line, or a drafting error.",
                        null, null, null, null, null, null, new List<Vec2> { seg.A, seg.B }, 400);
                }
            }
        }

        private static double ThicknessPenalty(QcWall c, QcWall r) => Math.Abs(c.Thickness - r.Thickness) * 0.2;

        private static bool AutoAligner_Intersect(Seg2 a, Seg2 b, out Vec2 p) => Alignment.AutoAligner.Intersect(a, b, out p);

        public static bool ThicknessOk(FloorContext ctx, double cad, QcWall r, out string revitText)
        {
            var s = ctx.Settings;
            revitText = r.CoreThickness.HasValue && Math.Abs(r.CoreThickness.Value - r.Thickness) > 0.5
                ? $"{Mm(r.Thickness)} total / {Mm(r.CoreThickness.Value)} core"
                : Mm(r.Thickness);
            string mode = (s.RevitWallWidthMode ?? "Auto").Trim().ToLowerInvariant();
            bool totalOk = Math.Abs(cad - r.Thickness) <= s.WallThicknessTol;
            bool coreOk = r.CoreThickness.HasValue && Math.Abs(cad - r.CoreThickness.Value) <= s.WallThicknessTol;
            bool eqOk = s.ThicknessEquivalents.Any(e => Math.Abs(e.Cad - cad) <= s.WallThicknessTol &&
                                                        e.Revit.Any(v => Math.Abs(v - r.Thickness) <= s.WallThicknessTol ||
                                                                         (r.CoreThickness.HasValue && Math.Abs(v - r.CoreThickness.Value) <= s.WallThicknessTol)));
            if (eqOk) return true;
            switch (mode)
            {
                case "total": return totalOk;
                case "core": return coreOk || (!r.CoreThickness.HasValue && totalOk);
                default: return totalOk || coreOk;
            }
        }
    }
}
