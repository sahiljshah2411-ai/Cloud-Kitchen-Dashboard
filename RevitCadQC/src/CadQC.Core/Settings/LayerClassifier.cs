using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace CadQC.Core.Settings
{
    public enum LayerCategory { Unknown, Ignore, Wall, Door, Window, Column, Grid, Room, Dimension, Annotation }

    /// <summary>
    /// Decides what every CAD layer is. Order of authority:
    ///   1. consultant profile  - exact layer names confirmed once in the review grid, then remembered
    ///   2. CadLayerDictionary.txt - Notepad-editable token rules, FIRST MATCH WINS
    ///   3. built-in rules made from the layer patterns in the QC settings
    /// Anything still unknown is simply not used (never guessed).
    /// </summary>
    public sealed class LayerClassifier
    {
        private readonly List<(LayerCategory cat, Regex rx)> _rules = new List<(LayerCategory, Regex)>();
        private readonly Dictionary<string, LayerCategory> _exact = new Dictionary<string, LayerCategory>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LayerCategory> _cache = new Dictionary<string, LayerCategory>(StringComparer.OrdinalIgnoreCase);

        public string Source { get; private set; } = "built-in rules";
        public CadLayerProfile Profile { get; private set; }

        public static LayerClassifier FromSettings(QcSettings s)
        {
            var c = new LayerClassifier();
            var dict = CadLayerDictionary.FindFile();
            if (dict != null && c.LoadDictionary(dict)) c.Source = "CadLayerDictionary.txt (" + dict + ")";
            else c.AddRulesFrom(s);
            return c;
        }

        /// <summary>Built-in order: specific before generic, so "A-DOOR-WALL" is a door and "WALL-HATCH" is ignored.</summary>
        public void AddRulesFrom(QcSettings s)
        {
            Add(LayerCategory.Ignore, s.ExcludeLayers);
            Add(LayerCategory.Dimension, s.DimensionLayers);
            Add(LayerCategory.Door, s.DoorLayers);
            Add(LayerCategory.Window, s.WindowLayers);
            Add(LayerCategory.Column, s.ColumnLayers);
            Add(LayerCategory.Grid, s.GridLayers);
            Add(LayerCategory.Wall, s.WallLayers);
            Add(LayerCategory.Room, s.RoomLayers);
        }

        private void Add(LayerCategory cat, string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return;
            try { _rules.Add((cat, new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))); }
            catch (ArgumentException) { _rules.Add((cat, new Regex(Regex.Escape(pattern), RegexOptions.IgnoreCase))); }
        }

        public bool LoadDictionary(string path)
        {
            try
            {
                int n = 0;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (!Enum.TryParse(line.Substring(0, eq).Trim(), true, out LayerCategory cat)) continue;
                    foreach (var token in line.Substring(eq + 1).Split('|'))
                    {
                        if (string.IsNullOrWhiteSpace(token)) continue;
                        Add(cat, token.Trim());
                        n++;
                    }
                }
                return n > 0;
            }
            catch { return false; }
        }

        public void UseProfile(CadLayerProfile p)
        {
            Profile = p;
            _exact.Clear();
            _cache.Clear();
            if (p == null) return;
            foreach (var kv in p.Layers)
                if (Enum.TryParse(kv.Value, true, out LayerCategory c)) _exact[kv.Key] = c;
        }

        public LayerCategory Classify(string layer)
        {
            if (string.IsNullOrEmpty(layer)) return LayerCategory.Unknown;
            if (_exact.TryGetValue(layer, out var e)) return e;
            if (_cache.TryGetValue(layer, out var c)) return c;
            c = LayerCategory.Unknown;
            foreach (var r in _rules)
                if (r.rx.IsMatch(layer)) { c = r.cat; break; }
            _cache[layer] = c;
            return c;
        }

        /// <summary>Rule-only answer (ignores the profile) - used to pre-fill the review grid.</summary>
        public LayerCategory ClassifyByRules(string layer)
        {
            foreach (var r in _rules)
                if (r.rx.IsMatch(layer ?? "")) return r.cat;
            return LayerCategory.Unknown;
        }
    }

    /// <summary>A consultant's layer convention, confirmed once in the review grid.</summary>
    public sealed class CadLayerProfile
    {
        public string Name { get; set; }
        public DateTime SavedUtc { get; set; } = DateTime.UtcNow;
        public string SavedBy { get; set; } = Environment.UserName;
        /// <summary>Layer name → category name.</summary>
        public Dictionary<string, string> Layers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Share of the drawing's (used) layers that this profile knows.</summary>
        public double Coverage(IEnumerable<string> drawingLayers)
        {
            var l = drawingLayers.Where(x => !string.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (l.Count == 0) return 0;
            return (double)l.Count(x => Layers.ContainsKey(x)) / l.Count;
        }
    }

    /// <summary>
    /// Consultant profiles live in %APPDATA%\RevitCadQC\CadProfiles - outside the tool folder, so reinstalling
    /// never deletes them. The team package carries a copy; the teammate installer adds missing ones.
    /// </summary>
    public static class CadProfileStore
    {
        public const double MatchThreshold = 0.6;

        public static string Folder
        {
            get
            {
                var d = Environment.GetEnvironmentVariable("CADQC_PROFILE_DIR");
                if (string.IsNullOrEmpty(d))
                    d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitCadQC", "CadProfiles");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static List<CadLayerProfile> LoadAll()
        {
            var res = new List<CadLayerProfile>();
            foreach (var f in Directory.GetFiles(Folder, "*.json"))
            {
                try
                {
                    var p = JsonConvert.DeserializeObject<CadLayerProfile>(File.ReadAllText(f));
                    if (p != null && p.Layers.Count > 0)
                    {
                        p.Layers = new Dictionary<string, string>(p.Layers, StringComparer.OrdinalIgnoreCase);
                        res.Add(p);
                    }
                }
                catch { /* a broken profile file never blocks QC */ }
            }
            return res;
        }

        public static CadLayerProfile BestMatch(IEnumerable<string> drawingLayers, out double coverage)
        {
            coverage = 0;
            CadLayerProfile best = null;
            var layers = drawingLayers.ToList();
            foreach (var p in LoadAll())
            {
                double c = p.Coverage(layers);
                if (c > coverage) { coverage = c; best = p; }
            }
            return coverage >= MatchThreshold ? best : null;
        }

        public static string Save(CadLayerProfile p)
        {
            var safe = Regex.Replace(p.Name ?? "Consultant", @"[^\w\- ]", "_").Trim();
            if (safe.Length == 0) safe = "Consultant";
            var path = Path.Combine(Folder, safe + ".json");
            // merge with an existing profile of the same name so several drawings of one consultant accumulate
            if (File.Exists(path))
            {
                try
                {
                    var old = JsonConvert.DeserializeObject<CadLayerProfile>(File.ReadAllText(path));
                    if (old != null)
                        foreach (var kv in old.Layers)
                            if (!p.Layers.ContainsKey(kv.Key)) p.Layers[kv.Key] = kv.Value;
                }
                catch { }
            }
            p.SavedUtc = DateTime.UtcNow;
            File.WriteAllText(path, JsonConvert.SerializeObject(p, Formatting.Indented));
            return path;
        }
    }

    /// <summary>Location and default content of CadLayerDictionary.txt.</summary>
    public static class CadLayerDictionary
    {
        public const string FileName = "CadLayerDictionary.txt";

        /// <summary>%APPDATA%\RevitCadQC first (your edits), then next to the add-in DLL (the shipped copy).</summary>
        public static string FindFile()
        {
            var user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitCadQC", FileName);
            if (File.Exists(user)) return user;
            try
            {
                var dir = Path.GetDirectoryName(typeof(CadLayerDictionary).Assembly.Location);
                var shipped = dir == null ? null : Path.Combine(dir, FileName);
                if (shipped != null && File.Exists(shipped)) return shipped;
            }
            catch { }
            return null;
        }
    }
}
