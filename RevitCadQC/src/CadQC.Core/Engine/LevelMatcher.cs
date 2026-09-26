using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CadQC.Core.Extraction;
using CadQC.Core.Model;

namespace CadQC.Core.Engine
{
    /// <summary>Maps floor indices (0 = ground, 1 = first, ... 1000 = roof) to Revit levels.</summary>
    public sealed class LevelMatcher
    {
        private readonly Dictionary<int, QcLevel> _byIndex = new Dictionary<int, QcLevel>();
        public List<string> Log { get; } = new List<string>();

        public LevelMatcher(IEnumerable<QcLevel> levels)
        {
            var stories = levels.Where(l => l.IsBuildingStory).OrderBy(l => l.ElevationMm).ToList();
            if (stories.Count == 0) stories = levels.OrderBy(l => l.ElevationMm).ToList();
            if (stories.Count == 0) return;

            var parsed = stories.Select(l => (l, f: TextParsing.ParseFloors(l.Name))).ToList();
            bool explicitGround = parsed.Any(p => p.f.Contains(0));
            bool genericNames = stories.All(l => Regex.IsMatch(l.Name ?? "", @"^\s*(level|lvl|l)\s*[-_ ]?\s*\d+\s*$", RegexOptions.IgnoreCase));
            int shift = 0;
            if (!explicitGround && genericNames)
            {
                // Revit template convention: "Level 1" is the ground floor
                var lowest = parsed.Where(p => p.f.Count == 1).OrderBy(p => Math.Abs(p.l.ElevationMm)).FirstOrDefault();
                if (lowest.l != null && lowest.f[0] == 1 && Math.Abs(lowest.l.ElevationMm) < 1500)
                {
                    shift = -1;
                    Log.Add("Revit levels use the 'Level 1 = ground' convention; CAD GROUND FLOOR maps to 'Level 1'.");
                }
            }
            foreach (var p in parsed)
                foreach (var f in p.f)
                {
                    int k = f == TextParsing.RoofIndex ? f : f + shift;
                    if (!_byIndex.ContainsKey(k)) _byIndex[k] = p.l;
                }

            // anything still unnamed: rank by elevation relative to the level closest to ±0
            if (!_byIndex.ContainsKey(0))
            {
                var ground = stories.OrderBy(l => Math.Abs(l.ElevationMm)).First();
                int gi = stories.IndexOf(ground);
                for (int i = 0; i < stories.Count; i++)
                    if (!_byIndex.ContainsKey(i - gi) && !_byIndex.ContainsValue(stories[i])) _byIndex[i - gi] = stories[i];
            }
            if (!_byIndex.ContainsKey(TextParsing.RoofIndex))
            {
                var top = stories.Last();
                if (Regex.IsMatch(top.Name ?? "", "ROOF|TERRACE", RegexOptions.IgnoreCase)) _byIndex[TextParsing.RoofIndex] = top;
            }
        }

        public QcLevel Find(int floorIndex) => _byIndex.TryGetValue(floorIndex, out var l) ? l : null;

        public static string FloorLabel(int f)
        {
            if (f == TextParsing.RoofIndex) return "Terrace / Roof";
            if (f == 0) return "Ground Floor";
            if (f < 0) return "Basement " + (-f);
            string suf = (f % 100 >= 11 && f % 100 <= 13) ? "th" : (f % 10 == 1 ? "st" : f % 10 == 2 ? "nd" : f % 10 == 3 ? "rd" : "th");
            return f + suf + " Floor";
        }

        public static string FloorCode(int f)
        {
            if (f == TextParsing.RoofIndex) return "RF";
            if (f < 0) return "B" + (-f);
            return f == 0 ? "GF" : "L" + f.ToString("00");
        }
    }
}
