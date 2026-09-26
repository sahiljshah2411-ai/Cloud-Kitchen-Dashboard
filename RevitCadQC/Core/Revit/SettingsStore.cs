using System;
using System.IO;
using Autodesk.Revit.DB;
using RevitCadQC.Core.Settings;

namespace RevitCadQC.Core.Revit
{
    /// <summary>
    /// QC settings live next to the Revit model ("&lt;model&gt;_CadQC.json") so every project keeps its own CAD folder,
    /// layer standard, floor mapping and tolerances. Unsaved models use %AppData%\RevitCadQC\default_settings.json,
    /// which is also the template for new projects.
    /// </summary>
    public static class SettingsStore
    {
        public static string AppDataFolder
        {
            get
            {
                var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitCadQC");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string DefaultPath => Path.Combine(AppDataFolder, "default_settings.json");

        public static string PathFor(Document doc)
        {
            if (doc == null || string.IsNullOrEmpty(doc.PathName)) return DefaultPath;
            try
            {
                var dir = Path.GetDirectoryName(doc.PathName);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return DefaultPath;
                return Path.Combine(dir, Path.GetFileNameWithoutExtension(doc.PathName) + "_CadQC.json");
            }
            catch { return DefaultPath; }
        }

        /// <summary>Messages from reading CadQcConfig.txt (shown in the run log).</summary>
        public static System.Collections.Generic.List<string> LastConfigLog { get; private set; } = new System.Collections.Generic.List<string>();

        public static QcSettings Load(Document doc)
        {
            // office rules (CadQcConfig.txt) → project / personal settings → enforced office rules ('!' lines)
            var s = new QcSettings();
            LastConfigLog = CadQcConfig.Apply(s, enforcedOnly: false);
            var p = PathFor(doc);
            var file = File.Exists(p) ? p : (File.Exists(DefaultPath) ? DefaultPath : null);
            if (file != null)
            {
                try { Newtonsoft.Json.JsonConvert.PopulateObject(File.ReadAllText(file), s, QcSettings.JsonOptions()); }
                catch (Exception ex) { LastConfigLog.Add("Settings file unreadable, defaults used: " + ex.Message); }
            }
            LastConfigLog.AddRange(CadQcConfig.Apply(s, enforcedOnly: true));
            s.Layers = null;
            return s;
        }

        public static void Save(Document doc, QcSettings s)
        {
            try
            {
                s.Save(PathFor(doc));
                // the latest settings become the template for the next project (without project-specific paths/mappings)
                var template = QcSettings.Load(PathFor(doc));
                template.CadFolder = "";
                template.OutputFolder = "";
                template.FloorMappings.Clear();
                template.Save(DefaultPath);
            }
            catch { /* read-only folder: settings still used for this run */ }
        }
    }
}
