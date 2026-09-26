using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace RevitCadQC.Core.Settings
{
    /// <summary>
    /// Central office rules (CadQcConfig.txt) - edited by the BIM lead only, read by every user's tool at run time.
    /// Looked up in: team package folder (Check for Update pointer) → %APPDATA%\RevitCadQC → next to the DLL.
    ///
    ///   KEY=VALUE     office default; a project's own settings file may change it
    ///   !KEY=VALUE    ENFORCED; always wins over project settings
    ///
    /// KEY is any QC setting name (e.g. WallThicknessTol, WallLayers, CheckDimensions) plus:
    ///   THICKNESS_EQUIV=230|250,254     CAD 230 accepted as Revit 250 or 254
    ///   ROOM_KEYWORD=LOUNGE             extra room name word
    /// Bad or unknown lines are ignored and reported, never fatal.
    /// </summary>
    public static class CadQcConfig
    {
        public const string FileName = "CadQcConfig.txt";

        public static string FindFile()
        {
            foreach (var dir in new[] { PackagePath.Current, AppData(), DllDir() })
            {
                if (string.IsNullOrEmpty(dir)) continue;
                var f = Path.Combine(dir, FileName);
                if (File.Exists(f)) return f;
            }
            return null;
        }

        /// <summary>Applies the central file. enforcedOnly = only the '!' lines (used after a project file was loaded).</summary>
        public static List<string> Apply(QcSettings s, bool enforcedOnly, string path = null)
        {
            var log = new List<string>();
            path = path ?? FindFile();
            if (path == null) return log;
            string[] lines;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var rd = new StreamReader(fs))
                    lines = rd.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            }
            catch (Exception ex) { log.Add("CadQcConfig.txt unreadable: " + ex.Message); return log; }

            bool equivReset = false;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                bool enforced = line.StartsWith("!", StringComparison.Ordinal);
                if (enforced) line = line.Substring(1).Trim();
                if (enforcedOnly && !enforced) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) { log.Add("CadQcConfig: ignored line '" + raw + "'"); continue; }
                var key = line.Substring(0, eq).Trim();
                var val = line.Substring(eq + 1).Trim();

                if (key.Equals("CONFIG_VERSION", StringComparison.OrdinalIgnoreCase)) continue;
                if (key.Equals("THICKNESS_EQUIV", StringComparison.OrdinalIgnoreCase))
                {
                    if (!equivReset) { s.ThicknessEquivalents.Clear(); equivReset = true; }
                    var parts = val.Split('|');
                    if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var cad))
                    {
                        var rv = parts[1].Split(',').Select(x => double.TryParse(x.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN)
                                         .Where(d => !double.IsNaN(d)).ToList();
                        if (rv.Count > 0) { s.ThicknessEquivalents.Add(new ThicknessEquivalent { Cad = cad, Revit = rv }); continue; }
                    }
                    log.Add("CadQcConfig: bad THICKNESS_EQUIV '" + val + "'");
                    continue;
                }
                if (key.Equals("ROOM_KEYWORD", StringComparison.OrdinalIgnoreCase))
                {
                    if (!s.RoomKeywords.Contains(val, StringComparer.OrdinalIgnoreCase)) s.RoomKeywords.Add(val.ToUpperInvariant());
                    continue;
                }

                var prop = typeof(QcSettings).GetProperty(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop == null || !prop.CanWrite) { log.Add("CadQcConfig: unknown setting '" + key + "'"); continue; }
                try
                {
                    object v;
                    var t = prop.PropertyType;
                    if (t == typeof(string)) v = val;
                    else if (t == typeof(bool)) v = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase) || val.Equals("yes", StringComparison.OrdinalIgnoreCase);
                    else if (t == typeof(double)) v = double.Parse(val, CultureInfo.InvariantCulture);
                    else if (t == typeof(int)) v = int.Parse(val, CultureInfo.InvariantCulture);
                    else if (t.IsEnum) v = Enum.Parse(t, val, true);
                    else { log.Add("CadQcConfig: '" + key + "' cannot be set from the text file"); continue; }
                    prop.SetValue(s, v);
                }
                catch { log.Add("CadQcConfig: bad value for '" + key + "': " + val); }
            }
            s.Layers = null; // layer rules may have changed
            return log;
        }

        private static string AppData() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitCadQC");

        private static string DllDir()
        {
            try { return Path.GetDirectoryName(typeof(CadQcConfig).Assembly.Location); }
            catch { return null; }
        }
    }

    /// <summary>
    /// Where the team package lives (network folder). Remembered in %APPDATA%\RevitCadQC\package_path.txt,
    /// written by the teammate installer. version.txt there can redirect with a NEWPATH= line.
    /// </summary>
    public static class PackagePath
    {
        private static string PointerFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitCadQC", "package_path.txt");

        public static string Current
        {
            get
            {
                try
                {
                    if (!File.Exists(PointerFile)) return null;
                    var p = File.ReadAllText(PointerFile).Trim();
                    return p.Length > 0 && Directory.Exists(p) ? p : null;
                }
                catch { return null; }
            }
        }

        public static void Set(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PointerFile));
                File.WriteAllText(PointerFile, path ?? "");
            }
            catch { }
        }
    }
}
