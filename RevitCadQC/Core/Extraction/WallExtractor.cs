using System;
using System.Collections.Generic;
using System.Linq;
using RevitCadQC.Core.Geometry;
using RevitCadQC.Core.Model;
using RevitCadQC.Core.Settings;

namespace RevitCadQC.Core.Extraction
{
    /// <summary>
    /// Recovers walls (centre line + thickness) from double-line CAD drafting.
    /// 1. wall-layer lines are grouped by direction and merged when collinear;
    /// 2. parallel lines whose spacing is a plausible wall thickness are paired, closest spacing first,
    ///    so a face line is only ever consumed once (handles corridors, cavity walls and shafts);
    /// 3. pieces are merged through T-junctions (one face continuous) but not through openings (both faces broken).
    /// </summary>
    public sealed class WallExtractor
    {
        private readonly QcSettings _s;

        public WallExtractor(QcSettings settings) { _s = settings; }

        public sealed class SourceLine
        {
            public Seg2 Seg;
            public string Layer;
            public string Handle;
        }

        private sealed class Line
        {
            public int Id;
            public double Lo, Hi, Off;
            public string Layer;
            public List<string> Handles = new List<string>();
            public List<Interval> Used = new List<Interval>();
            public int DuplicateCount;
        }

        private sealed class Group
        {
            public double Angle;
            public Vec2 U, N;
            public List<Line> Lines = new List<Line>();
        }

        public sealed class Result
        {
            public List<QcWall> Walls = new List<QcWall>();
            public List<Seg2> Unpaired = new List<Seg2>();
            public int DuplicateLines;
        }

        public Result Extract(IList<SourceLine> input)
        {
            var result = new Result();
            double angTol = _s.ParallelAngleTolDeg * GeoMath.Deg;
            var src = input.Where(l => l.Seg.Length >= _s.MinSegmentLength).ToList();
            if (src.Count == 0) return result;

            var groups = BuildGroups(src, angTol);
            int wallNo = 0;
            foreach (var g in groups)
            {
                MergeCollinear(g, result);
                var pieces = PairLines(g);
                pieces = MergePieces(g, pieces);
                foreach (var p in pieces)
                {
                    var a = g.U * p.Lo + g.N * p.Off;
                    var b = g.U * p.Hi + g.N * p.Off;
                    result.Walls.Add(new QcWall
                    {
                        Id = "CW" + (++wallNo).ToString("0000"),
                        Centerline = new Seg2(a, b),
                        Thickness = Math.Round(p.T, 1),
                        Layer = p.Layer,
                        Handles = p.Handles.Distinct().ToList()
                    });
                }
                foreach (var l in g.Lines)
                {
                    foreach (var free in GeoMath.Uncovered(l.Lo, l.Hi, l.Used))
                        if (free.Length >= _s.MinIssueLength)
                            result.Unpaired.Add(new Seg2(g.U * free.Lo + g.N * l.Off, g.U * free.Hi + g.N * l.Off));
                }
            }
            return result;
        }

        private List<Group> BuildGroups(List<SourceLine> src, double angTol)
        {
            // Normalise angles to (-tol, π - tol] so near-horizontal lines at 179.9° join those at 0°.
            var items = src.Select(l =>
            {
                double a = l.Seg.Angle;
                if (a > Math.PI - angTol) a -= Math.PI;
                return (a, l);
            }).OrderBy(t => t.a).ToList();

            var groups = new List<Group>();
            var current = new List<(double a, SourceLine l)>();
            double start = double.NaN;
            foreach (var it in items)
            {
                if (current.Count > 0 && it.a - start > angTol)
                {
                    groups.Add(MakeGroup(current));
                    current = new List<(double, SourceLine)>();
                }
                if (current.Count == 0) start = it.a;
                current.Add(it);
            }
            if (current.Count > 0) groups.Add(MakeGroup(current));
            return groups;
        }

        private static Group MakeGroup(List<(double a, SourceLine l)> items)
        {
            // length-weighted mean direction
            double wsum = 0, asum = 0;
            foreach (var it in items) { double w = it.l.Seg.Length; wsum += w; asum += it.a * w; }
            double ang = asum / wsum;
            var g = new Group { Angle = GeoMath.NormalizeUndirected(ang), U = Vec2.FromAngle(ang) };
            g.N = g.U.Perp;
            int id = 0;
            foreach (var it in items)
            {
                double t1 = it.l.Seg.A.Dot(g.U), t2 = it.l.Seg.B.Dot(g.U);
                double off = (it.l.Seg.A.Dot(g.N) + it.l.Seg.B.Dot(g.N)) / 2;
                var line = new Line { Id = id++, Lo = Math.Min(t1, t2), Hi = Math.Max(t1, t2), Off = off, Layer = it.l.Layer };
                if (!string.IsNullOrEmpty(it.l.Handle)) line.Handles.Add(it.l.Handle);
                g.Lines.Add(line);
            }
            return g;
        }

        private void MergeCollinear(Group g, Result result)
        {
            var sorted = g.Lines.OrderBy(l => l.Off).ThenBy(l => l.Lo).ToList();
            var merged = new List<Line>();
            int i = 0;
            while (i < sorted.Count)
            {
                // band of lines with (nearly) the same offset
                int j = i;
                double baseOff = sorted[i].Off;
                while (j + 1 < sorted.Count && sorted[j + 1].Off - baseOff <= _s.CollinearTol) j++;
                var band = sorted.Skip(i).Take(j - i + 1).OrderBy(l => l.Lo).ToList();
                double off = band.Sum(l => l.Off * (l.Hi - l.Lo)) / Math.Max(1e-9, band.Sum(l => l.Hi - l.Lo));
                Line cur = null;
                foreach (var l in band)
                {
                    if (cur != null && l.Lo <= cur.Hi + _s.MergeGapTol)
                    {
                        if (l.Lo < cur.Hi - 1 && l.Hi > cur.Lo + 1) { cur.DuplicateCount++; result.DuplicateLines++; }
                        cur.Hi = Math.Max(cur.Hi, l.Hi);
                        cur.Handles.AddRange(l.Handles);
                    }
                    else
                    {
                        cur = new Line { Id = merged.Count, Lo = l.Lo, Hi = l.Hi, Off = off, Layer = l.Layer, Handles = new List<string>(l.Handles) };
                        merged.Add(cur);
                    }
                }
                i = j + 1;
            }
            g.Lines = merged;
        }

        private sealed class Piece
        {
            public double Lo, Hi, Off, T;
            public Line A, B;
            public string Layer;
            public List<string> Handles = new List<string>();
        }

        private List<Piece> PairLines(Group g)
        {
            var lines = g.Lines.OrderBy(l => l.Off).ToList();
            var cands = new List<(Line a, Line b, double d, double lo, double hi)>();
            for (int i = 0; i < lines.Count; i++)
            {
                for (int j = i + 1; j < lines.Count; j++)
                {
                    double d = lines[j].Off - lines[i].Off;
                    if (d > _s.MaxWallThickness) break;
                    if (d < _s.MinWallThickness) continue;
                    double lo = Math.Max(lines[i].Lo, lines[j].Lo), hi = Math.Min(lines[i].Hi, lines[j].Hi);
                    if (hi - lo < MinOverlapFor(lines[i], lines[j])) continue;
                    cands.Add((lines[i], lines[j], d, lo, hi));
                }
            }

            var pieces = new List<Piece>();
            foreach (var c in cands.OrderBy(c => c.d))
            {
                var used = c.a.Used.Concat(c.b.Used);
                foreach (var free in GeoMath.Uncovered(c.lo, c.hi, used))
                {
                    if (free.Length < Math.Min(MinOverlapFor(c.a, c.b), _s.MinPairOverlap)) continue;
                    c.a.Used.Add(free);
                    c.b.Used.Add(free);
                    var p = new Piece { Lo = free.Lo, Hi = free.Hi, Off = (c.a.Off + c.b.Off) / 2, T = c.d, A = c.a, B = c.b, Layer = c.a.Layer ?? c.b.Layer };
                    p.Handles.AddRange(c.a.Handles);
                    p.Handles.AddRange(c.b.Handles);
                    pieces.Add(p);
                }
            }
            return pieces;
        }

        private double MinOverlapFor(Line a, Line b)
        {
            double shorter = Math.Min(a.Hi - a.Lo, b.Hi - b.Lo);
            return Math.Max(40, Math.Min(_s.MinPairOverlap, shorter * 0.8));
        }

        private List<Piece> MergePieces(Group g, List<Piece> pieces)
        {
            var res = new List<Piece>();
            var bands = pieces.OrderBy(p => p.Off).ThenBy(p => p.T).ThenBy(p => p.Lo).ToList();
            var used = new bool[bands.Count];
            for (int i = 0; i < bands.Count; i++)
            {
                if (used[i]) continue;
                var cur = bands[i];
                used[i] = true;
                bool grew;
                do
                {
                    grew = false;
                    for (int j = 0; j < bands.Count; j++)
                    {
                        if (used[j]) continue;
                        var o = bands[j];
                        if (Math.Abs(o.Off - cur.Off) > Math.Max(2, _s.CollinearTol) || Math.Abs(o.T - cur.T) > 2) continue;
                        double gapLo, gapHi;
                        if (o.Lo >= cur.Hi) { gapLo = cur.Hi; gapHi = o.Lo; }
                        else if (o.Hi <= cur.Lo) { gapLo = o.Hi; gapHi = cur.Lo; }
                        else { gapLo = gapHi = 0; }
                        double gap = gapHi - gapLo;
                        bool join = gap <= _s.MergeGapTol || (gap <= _s.MaxWallThickness + 10 && FaceSpansGap(g, cur, gapLo, gapHi));
                        if (!join) continue;
                        cur = new Piece
                        {
                            Lo = Math.Min(cur.Lo, o.Lo),
                            Hi = Math.Max(cur.Hi, o.Hi),
                            Off = (cur.Off * (cur.Hi - cur.Lo) + o.Off * (o.Hi - o.Lo)) / Math.Max(1e-9, (cur.Hi - cur.Lo) + (o.Hi - o.Lo)),
                            T = (cur.T * (cur.Hi - cur.Lo) + o.T * (o.Hi - o.Lo)) / Math.Max(1e-9, (cur.Hi - cur.Lo) + (o.Hi - o.Lo)),
                            Layer = cur.Layer,
                            Handles = cur.Handles.Concat(o.Handles).ToList(),
                            A = cur.A,
                            B = cur.B
                        };
                        used[j] = true;
                        grew = true;
                    }
                } while (grew);
                res.Add(cur);
            }
            return res;
        }

        /// <summary>True when one of the wall's face lines runs continuously across the gap (a T-junction, not an opening).</summary>
        private bool FaceSpansGap(Group g, Piece p, double lo, double hi)
        {
            double half = p.T / 2;
            foreach (var l in g.Lines)
            {
                bool isFace = Math.Abs(l.Off - (p.Off - half)) <= _s.CollinearTol + 1 || Math.Abs(l.Off - (p.Off + half)) <= _s.CollinearTol + 1;
                if (isFace && l.Lo <= lo + 1 && l.Hi >= hi - 1) return true;
            }
            return false;
        }

        /// <summary>Links thickness notes ("230 THK") to the nearest parallel wall.</summary>
        public static void ApplyThicknessNotes(IList<QcWall> walls, IEnumerable<Dxf.CadText> texts, double radius)
        {
            foreach (var t in texts)
            {
                var thk = TextParsing.ParseThicknessNote(t.Text);
                if (thk == null) continue;
                QcWall best = null;
                double bestScore = double.MaxValue;
                foreach (var w in walls)
                {
                    double d = w.Centerline.DistanceToPoint(t.Position);
                    if (d > radius) continue;
                    double angPenalty = GeoMath.UndirectedDiff(w.Centerline.Angle, t.Rotation) < 5 * GeoMath.Deg ? 0 : radius * 0.35;
                    double thkPenalty = Math.Abs(w.Thickness - thk.Value) < 3 ? 0 : 50;
                    double score = d + angPenalty + thkPenalty;
                    if (score < bestScore) { bestScore = score; best = w; }
                }
                if (best != null && best.AnnotatedThickness == null)
                {
                    best.AnnotatedThickness = thk;
                    best.AnnotationText = t.Text;
                }
            }
        }
    }
}
