using System;
using System.Collections.Generic;
using System.Linq;
using RevitCadQC.Core.Geometry;
using RevitCadQC.Core.Model;
using RevitCadQC.Core.Settings;

namespace RevitCadQC.Core.Alignment
{
    public sealed class AlignmentResult
    {
        public Similarity2 Transform { get; set; } = Similarity2.Identity;
        public string Method { get; set; }
        public double RmsMm { get; set; }
        public double InlierRatio { get; set; }
        public int Samples { get; set; }
        public bool Reliable { get; set; }
        public List<string> Log { get; set; } = new List<string>();
        public override string ToString() => $"{Method}: {Transform} inliers={InlierRatio:P0} rms={RmsMm:0.0}mm";
    }

    /// <summary>
    /// Finds the CAD→Revit transform for one floor without user input:
    ///  • identity (CAD linked origin-to-origin or already in project coordinates);
    ///  • grid names (intersections of matching named grids, the most reliable);
    ///  • geometric best fit: coarse starts (extent centres × 4 rotations + dominant-direction rotation)
    ///    refined by point-to-line ICP against Revit wall centre lines.
    /// The winner is the candidate with the highest share of CAD wall samples lying on a Revit wall.
    /// </summary>
    public sealed class AutoAligner
    {
        private readonly QcSettings _s;
        private List<Seg2> _target;
        private SpatialHash<int> _targetHash;
        private List<Vec2> _cadSamples;

        public AutoAligner(QcSettings s) { _s = s; }

        public AlignmentResult Align(CadFloorData cad, IList<QcWall> revitWalls, IList<QcGrid> revitGrids, FloorMapping manual)
        {
            var res = new AlignmentResult();
            _target = revitWalls.Where(w => w.Centerline != null && w.Centerline.Length > 50).Select(w => w.Centerline).ToList();
            _targetHash = new SpatialHash<int>(500);
            for (int i = 0; i < _target.Count; i++)
                foreach (var p in Sample(_target[i], 100)) _targetHash.Add(p, i);
            _cadSamples = cad.Walls.SelectMany(w => Sample(w.Centerline, 250)).ToList();
            if (_cadSamples.Count > 6000) _cadSamples = _cadSamples.Where((p, i) => i % (_cadSamples.Count / 6000 + 1) == 0).ToList();
            res.Samples = _cadSamples.Count;

            var cands = new List<(string method, Similarity2 t)>();

            if (manual != null && (manual.ManualOffsetX.HasValue || manual.ManualOffsetY.HasValue || manual.ManualRotationDeg.HasValue))
            {
                var m = new Similarity2 { Rotation = (manual.ManualRotationDeg ?? 0) * GeoMath.Deg, Tx = manual.ManualOffsetX ?? 0, Ty = manual.ManualOffsetY ?? 0 };
                string label = manual.FromModelLink ? "Manual: Revit CAD link placement" : "Manual (from floor mapping)";
                if (_s.Alignment == AlignmentMode.Manual)
                    return Evaluate(label, m, res);
                cands.Add((label, m));
            }
            if (_s.Alignment == AlignmentMode.Identity) return Evaluate("Identity (origin to origin)", Similarity2.Identity, res);

            cands.Add(("Identity (origin to origin)", Similarity2.Identity));

            var byGrid = FromGrids(cad.Grids, revitGrids, res.Log);
            if (byGrid != null)
            {
                if (_s.Alignment == AlignmentMode.Grids) return Evaluate("Grid intersections", byGrid, res);
                cands.Add(("Grid intersections", byGrid));
            }

            if (_s.Alignment != AlignmentMode.Grids && _target.Count > 0 && _cadSamples.Count > 0)
            {
                var cadBox = Box2.FromPoints(_cadSamples);
                var rvtBox = Box2.FromPoints(_target.SelectMany(t => new[] { t.A, t.B }));
                var rots = new List<double> { 0, Math.PI / 2, Math.PI, -Math.PI / 2 };
                double dRot = DominantAngle(_target) - DominantAngle(cad.Walls.Select(w => w.Centerline).ToList());
                foreach (var k in new[] { 0, 1, 2, 3 }) rots.Add(dRot + k * Math.PI / 2);
                foreach (var r in rots.Distinct())
                {
                    var c = cadBox.Center.Rotate(r);
                    cands.Add(("Best fit", new Similarity2 { Rotation = r, Tx = rvtBox.Center.X - c.X, Ty = rvtBox.Center.Y - c.Y }));
                }
            }

            AlignmentResult best = null;
            foreach (var c in cands)
            {
                var refined = c.method.StartsWith("Identity") || c.method.StartsWith("Manual") ? c.t : Icp(c.t);
                // identity may also be close but slightly off: refine it separately and keep both
                var r1 = Evaluate(c.method, refined, new AlignmentResult { Samples = res.Samples });
                if (best == null || Better(r1, best)) best = r1;
                if (c.method.StartsWith("Identity") || c.method.StartsWith("Manual"))
                {
                    var r2 = Evaluate(c.method + " + ICP refinement", Icp(c.t, startRadius: 400), new AlignmentResult { Samples = res.Samples });
                    if (Better(r2, best) && r2.InlierRatio > r1.InlierRatio + 0.02) best = r2;
                }
            }
            if (best == null) return Evaluate("Identity (no Revit walls to align to)", Similarity2.Identity, res);
            best.Log.InsertRange(0, res.Log);
            return best;
        }

        private static bool Better(AlignmentResult a, AlignmentResult b) =>
            a.InlierRatio > b.InlierRatio + 0.01 || (Math.Abs(a.InlierRatio - b.InlierRatio) <= 0.01 && a.RmsMm < b.RmsMm);

        private AlignmentResult Evaluate(string method, Similarity2 t, AlignmentResult r)
        {
            r.Method = method;
            r.Transform = t;
            if (_cadSamples == null || _cadSamples.Count == 0 || _target == null || _target.Count == 0)
            {
                r.Reliable = method.StartsWith("Grid") || method.StartsWith("Manual");
                return r;
            }
            int inl = 0;
            double sq = 0;
            foreach (var p in _cadSamples)
            {
                var q = t.Apply(p);
                if (NearestOnTarget(q, 60, out _, out double d)) { inl++; sq += d * d; }
            }
            r.InlierRatio = (double)inl / _cadSamples.Count;
            r.RmsMm = inl > 0 ? Math.Sqrt(sq / inl) : double.MaxValue;
            r.Reliable = r.InlierRatio >= 0.5 && r.RmsMm <= _s.AlignmentAcceptRms;
            return r;
        }

        private Similarity2 Icp(Similarity2 start, double startRadius = 3000)
        {
            var cur = start;
            double radius = startRadius;
            for (int iter = 0; iter < 40; iter++)
            {
                double ata00 = 0, ata01 = 0, ata02 = 0, ata11 = 0, ata12 = 0, ata22 = 0, atb0 = 0, atb1 = 0, atb2 = 0;
                int n = 0;
                foreach (var p0 in _cadSamples)
                {
                    var p = cur.Apply(p0);
                    if (!NearestOnTarget(p, radius, out var seg, out _)) continue;
                    var nrm = seg.Dir.Perp;
                    var foot = seg.PointAt(GeoMath.Clamp(seg.Project(p), 0, seg.Length));
                    // residual r = n·(p - foot) + θ n·p⊥ + n·t  (small-angle linearisation)
                    double a0 = nrm.Dot(p.Perp), a1 = nrm.X, a2 = nrm.Y;
                    double b = -nrm.Dot(p - foot);
                    ata00 += a0 * a0; ata01 += a0 * a1; ata02 += a0 * a2; ata11 += a1 * a1; ata12 += a1 * a2; ata22 += a2 * a2;
                    atb0 += a0 * b; atb1 += a1 * b; atb2 += a2 * b;
                    n++;
                }
                if (n < 6) break;
                var x = Solve3(ata00 + 1e-3, ata01, ata02, ata11 + 1e-9, ata12, ata22 + 1e-9, atb0, atb1, atb2);
                if (x == null) break;
                var step = new Similarity2 { Rotation = x[0], Tx = x[1], Ty = x[2] };
                // rotation about origin in current frame: p' = R p + t
                cur = step.After(cur);
                double move = Math.Abs(x[0]) * 50000 + Math.Sqrt(x[1] * x[1] + x[2] * x[2]);
                if (move < 0.05 && radius <= 150) break;
                radius = Math.Max(120, radius * 0.6);
            }
            return cur;
        }

        private static double[] Solve3(double a00, double a01, double a02, double a11, double a12, double a22, double b0, double b1, double b2)
        {
            // symmetric 3x3 via Cramer's rule
            double det = a00 * (a11 * a22 - a12 * a12) - a01 * (a01 * a22 - a12 * a02) + a02 * (a01 * a12 - a11 * a02);
            if (Math.Abs(det) < 1e-12) return null;
            double x0 = (b0 * (a11 * a22 - a12 * a12) - a01 * (b1 * a22 - a12 * b2) + a02 * (b1 * a12 - a11 * b2)) / det;
            double x1 = (a00 * (b1 * a22 - a12 * b2) - b0 * (a01 * a22 - a12 * a02) + a02 * (a01 * b2 - b1 * a02)) / det;
            double x2 = (a00 * (a11 * b2 - b1 * a12) - a01 * (a01 * b2 - b1 * a02) + b0 * (a01 * a12 - a11 * a02)) / det;
            return new[] { x0, x1, x2 };
        }

        private bool NearestOnTarget(Vec2 p, double radius, out Seg2 seg, out double dist)
        {
            seg = null; dist = double.MaxValue;
            var seen = new HashSet<int>();
            foreach (var hit in _targetHash.Query(p, radius + 60))
            {
                if (!seen.Add(hit.item)) continue;
                double d = _target[hit.item].DistanceToPoint(p);
                if (d < dist) { dist = d; seg = _target[hit.item]; }
            }
            return seg != null && dist <= radius;
        }

        private static IEnumerable<Vec2> Sample(Seg2 s, double step)
        {
            int n = Math.Max(1, (int)(s.Length / step));
            for (int i = 0; i <= n; i++) yield return s.A + (s.B - s.A) * ((double)i / n);
        }

        private static double DominantAngle(IList<Seg2> segs)
        {
            // length-weighted histogram of directions modulo 90°
            var bins = new double[180];
            foreach (var s in segs)
            {
                double a = s.Angle % (Math.PI / 2);
                int b = (int)(a / (Math.PI / 2) * 180) % 180;
                bins[b] += s.Length;
            }
            int best = Array.IndexOf(bins, bins.Max());
            return (best + 0.5) / 180.0 * (Math.PI / 2);
        }

        private static Similarity2 FromGrids(IList<QcGrid> cad, IList<QcGrid> rvt, List<string> log)
        {
            var named = cad.Where(g => !string.IsNullOrEmpty(g.Name)).ToList();
            var pairs = new List<(QcGrid c, QcGrid r)>();
            foreach (var c in named)
            {
                var r = rvt.FirstOrDefault(x => string.Equals(Norm(x.Name), Norm(c.Name), StringComparison.OrdinalIgnoreCase));
                if (r != null) pairs.Add((c, r));
            }
            if (pairs.Count < 2) return null;
            var src = new List<Vec2>();
            var dst = new List<Vec2>();
            for (int i = 0; i < pairs.Count; i++)
                for (int j = i + 1; j < pairs.Count; j++)
                {
                    if (GeoMath.UndirectedDiff(pairs[i].c.Line.Angle, pairs[j].c.Line.Angle) < 10 * GeoMath.Deg) continue;
                    if (Intersect(pairs[i].c.Line, pairs[j].c.Line, out var pc) && Intersect(pairs[i].r.Line, pairs[j].r.Line, out var pr))
                    {
                        src.Add(pc); dst.Add(pr);
                    }
                }
            if (src.Count < 1) return null;
            Similarity2 t;
            if (src.Count == 1)
            {
                // one intersection: take rotation from the grid direction
                var c0 = pairs[0].c.Line; var r0 = pairs[0].r.Line;
                double rot = Math.Atan2(r0.Dir.Y, r0.Dir.X) - Math.Atan2(c0.Dir.Y, c0.Dir.X);
                var cr = src[0].Rotate(rot);
                t = new Similarity2 { Rotation = rot, Tx = dst[0].X - cr.X, Ty = dst[0].Y - cr.Y };
                {
                    // grids may be drawn in opposite directions: keep the variant that also lands the second grid
                    var alt = new Similarity2 { Rotation = rot + Math.PI };
                    var ca = src[0].Rotate(alt.Rotation);
                    alt.Tx = dst[0].X - ca.X; alt.Ty = dst[0].Y - ca.Y;
                    var probe = pairs[1].c.Line.Mid;
                    if (pairs[1].r.Line.DistanceToPoint(alt.Apply(probe)) < pairs[1].r.Line.DistanceToPoint(t.Apply(probe))) t = alt;
                }
            }
            else t = Similarity2.Fit(src, dst);
            log.Add($"Grid alignment from {pairs.Count} matching grid names, {src.Count} intersections: {t}");
            return t;
        }

        private static string Norm(string s) => (s ?? "").Trim().Replace(" ", "").ToUpperInvariant();

        public static bool Intersect(Seg2 a, Seg2 b, out Vec2 p)
        {
            p = default;
            var d1 = b.A - a.A;
            var r = a.B - a.A;
            var s = b.B - b.A;
            double den = r.Cross(s);
            if (Math.Abs(den) < 1e-9) return false;
            double t = d1.Cross(s) / den;
            p = a.A + r * t;
            return true;
        }
    }
}
