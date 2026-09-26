using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CadQC.Core.Dxf;
using CadQC.Core.Geometry;
using CadQC.Core.Settings;

namespace CadQC.Core.Extraction
{
    /// <summary>One floor plan found in a CAD file (a whole file, or one plan of a multi-plan sheet).</summary>
    public sealed class CadPlanRegion
    {
        public string File { get; set; }
        public string Title { get; set; }
        public List<int> Floors { get; set; } = new List<int>();
        public Box2? Region { get; set; }
        public override string ToString() => $"{Path.GetFileName(File)} [{Title ?? "whole drawing"}] floors={string.Join(",", Floors)}";
    }

    public static class DrawingPreparer
    {
        private static readonly double[] Common = { 75, 100, 110, 115, 125, 150, 175, 200, 225, 230, 250, 300, 350, 380, 450 };

        /// <summary>Works out the drawing unit and scales everything to millimetres. Returns a note for the report.</summary>
        public static string NormaliseUnits(CadDrawing dwg, QcSettings s, out double factor)
        {
            double headerFactor = DxfReader.UnitsToMm(dwg.InsUnits);
            double forced = s.CadUnitsToMm(-1);
            if (forced > 0)
            {
                dwg.ScaleInPlace(forced);
                factor = forced;
                return $"CAD units forced by settings ({s.CadUnits}).";
            }

            var spacings = SampleParallelSpacings(dwg, s);
            var candidates = new[] { 1.0, 10.0, 1000.0, 25.4, 304.8 };
            double best = headerFactor, bestScore = -1;
            var scores = new Dictionary<double, double>();
            foreach (var f in candidates)
            {
                // count spacings that land on a standard wall/column size (relative tolerance, so 9" = 228.6 mm counts as 230)
                double score = 0;
                foreach (var d in spacings)
                {
                    double mm = d * f;
                    if (Common.Any(c => Math.Abs(c - mm) <= c * 0.025)) score += 1.0;
                }
                scores[f] = score;
                if (score > bestScore) { bestScore = score; best = f; }
            }
            double chosen = headerFactor;
            string note;
            bool headerKnown = dwg.InsUnits == 1 || dwg.InsUnits == 2 || dwg.InsUnits == 4 || dwg.InsUnits == 5 || dwg.InsUnits == 6 || dwg.InsUnits == 14;
            if (spacings.Count < 10)
            {
                note = headerKnown ? $"Units from drawing header (INSUNITS={dwg.InsUnits})." : "Units unknown and too little wall linework to detect them; assumed millimetres.";
            }
            else if (headerKnown && scores.TryGetValue(headerFactor, out var hs) && hs >= 0.7 * bestScore)
            {
                note = $"Units from drawing header (INSUNITS={dwg.InsUnits}), confirmed by wall spacing analysis.";
            }
            else
            {
                chosen = best;
                note = $"Units detected from wall spacing analysis: 1 drawing unit = {best} mm" +
                       (headerKnown ? $" (header said INSUNITS={dwg.InsUnits}, which did not fit the wall thicknesses)." : ".");
            }
            if (Environment.GetEnvironmentVariable("CADQC_DEBUG_UNITS") == "1")
                note += " [scores: " + string.Join(", ", scores.Select(kv => kv.Key + "=" + kv.Value.ToString("0.##"))) + "; spacings: " + string.Join(",", spacings.Take(40).Select(x => x.ToString("0.###"))) + "]";
            dwg.ScaleInPlace(chosen);
            factor = chosen;
            return note;
        }

        private static List<double> SampleParallelSpacings(CadDrawing dwg, QcSettings s)
        {
            var segs = dwg.Curves.Where(c => s.Matches(s.WallLayers, c.Layer) && !s.IsExcluded(c.Layer)).SelectMany(c => c.Segments()).ToList();
            if (segs.Count < 20) segs = dwg.Curves.Where(c => !s.IsExcluded(c.Layer)).SelectMany(c => c.Segments()).ToList();
            segs = segs.Where(x => x.Length > 1e-6).OrderByDescending(x => x.Length).Take(2500).ToList();
            var res = new List<double>();
            double angTol = 0.5 * GeoMath.Deg;
            for (int i = 0; i < segs.Count; i++)
            {
                double bestD = double.MaxValue;
                for (int j = 0; j < segs.Count; j++)
                {
                    if (i == j) continue;
                    if (GeoMath.UndirectedDiff(segs[i].Angle, segs[j].Angle) > angTol) continue;
                    double d = Math.Abs(segs[i].SignedOffset(segs[j].Mid));
                    if (d < 1e-6 || d >= bestD) continue;
                    double t1 = segs[i].Project(segs[j].A), t2 = segs[i].Project(segs[j].B);
                    double lo = Math.Max(0, Math.Min(t1, t2)), hi = Math.Min(segs[i].Length, Math.Max(t1, t2));
                    if (hi - lo < Math.Min(segs[i].Length, segs[j].Length) * 0.5) continue;
                    bestD = d;
                }
                if (bestD < double.MaxValue) res.Add(bestD);
            }
            return res;
        }

        /// <summary>
        /// Finds the floor plans inside a CAD file. A file named "2ND FLOOR.dwg" is one plan; a sheet that
        /// holds several plans with titles such as "GROUND FLOOR PLAN" / "FIRST FLOOR PLAN" is split into regions.
        /// </summary>
        public static List<CadPlanRegion> FindPlans(CadDrawing dwg, QcSettings s)
        {
            var fileFloors = TextParsing.ParseFloors(Path.GetFileNameWithoutExtension(dwg.SourcePath ?? ""));
            var titles = dwg.Texts.Where(t => TextParsing.LooksLikePlanTitle(t.Text))
                                  .Select(t => (t, floors: TextParsing.ParseFloors(t.Text)))
                                  .Where(x => x.floors.Count > 0).ToList();
            // keep the most prominent title for each floor set (largest text)
            titles = titles.GroupBy(x => string.Join(",", x.floors)).Select(g => g.OrderByDescending(x => x.t.Height).First()).ToList();

            if (!s.SplitMultiPlanDrawings || titles.Count < 2)
            {
                var floors = fileFloors.Count > 0 ? fileFloors : (titles.Count == 1 ? titles[0].floors : new List<int>());
                return new List<CadPlanRegion> { new CadPlanRegion { File = dwg.SourcePath, Floors = floors, Title = titles.Count == 1 ? titles[0].t.Text : null } };
            }

            var islands = FindIslands(dwg, s);
            if (islands.Count < 2)
                return new List<CadPlanRegion> { new CadPlanRegion { File = dwg.SourcePath, Floors = fileFloors, Title = null } };

            var assigned = new Dictionary<int, Box2>();
            foreach (var isl in islands)
            {
                int bestT = -1;
                double bd = double.MaxValue;
                for (int k = 0; k < titles.Count; k++)
                {
                    var p = titles[k].t.Position;
                    double d = isl.DistanceTo(p);
                    // titles normally sit below their plan: favour that
                    if (p.Y < isl.MinY && p.X >= isl.MinX - 2000 && p.X <= isl.MaxX + 2000) d *= 0.5;
                    if (d < bd) { bd = d; bestT = k; }
                }
                if (bestT < 0 || bd > Math.Max(20000, Math.Max(isl.Width, isl.Height))) continue;
                assigned[bestT] = assigned.TryGetValue(bestT, out var b) ? b.Union(isl) : isl;
            }
            var res = new List<CadPlanRegion>();
            foreach (var kv in assigned)
                res.Add(new CadPlanRegion { File = dwg.SourcePath, Title = titles[kv.Key].t.Text, Floors = titles[kv.Key].floors, Region = kv.Value.Inflate(2500) });
            return res.Count > 0 ? res : new List<CadPlanRegion> { new CadPlanRegion { File = dwg.SourcePath, Floors = fileFloors } };
        }

        /// <summary>Connected islands of wall linework on a coarse occupancy raster.</summary>
        private static List<Box2> FindIslands(CadDrawing dwg, QcSettings s)
        {
            var segs = dwg.Curves.Where(c => s.Matches(s.WallLayers, c.Layer) && !s.IsExcluded(c.Layer)).SelectMany(c => c.Segments()).ToList();
            if (segs.Count < 10) segs = dwg.Curves.Where(c => !s.IsExcluded(c.Layer)).SelectMany(c => c.Segments()).ToList();
            if (segs.Count == 0) return new List<Box2>();
            const double cell = 1500;
            var occ = new HashSet<(int, int)>();
            foreach (var sg in segs)
            {
                int n = Math.Max(1, (int)(sg.Length / (cell / 2)));
                for (int i = 0; i <= n; i++)
                {
                    var p = sg.A + (sg.B - sg.A) * ((double)i / n);
                    occ.Add(((int)Math.Floor(p.X / cell), (int)Math.Floor(p.Y / cell)));
                }
            }
            var seen = new HashSet<(int, int)>();
            var islands = new List<Box2>();
            foreach (var start in occ)
            {
                if (seen.Contains(start)) continue;
                var q = new Queue<(int, int)>();
                q.Enqueue(start);
                seen.Add(start);
                var box = Box2.Empty;
                int count = 0;
                while (q.Count > 0)
                {
                    var (x, y) = q.Dequeue();
                    count++;
                    box = box.Union(new Box2(x * cell, y * cell, (x + 1) * cell, (y + 1) * cell));
                    for (int dx = -2; dx <= 2; dx++)
                        for (int dy = -2; dy <= 2; dy++)
                        {
                            var nb = (x + dx, y + dy);
                            if (occ.Contains(nb) && seen.Add(nb)) q.Enqueue(nb);
                        }
                }
                if (count >= 6) islands.Add(box);
            }
            return islands;
        }
    }
}
