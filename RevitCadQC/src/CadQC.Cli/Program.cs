using System;
using System.IO;
using System.Linq;
using CadQC.Core.Engine;
using CadQC.Core.Model;
using CadQC.Core.Report;
using CadQC.Core.Settings;
using Newtonsoft.Json;

namespace CadQC.Cli
{
    /// <summary>
    /// Command-line runner. Uses a model snapshot exported by the Revit add-in ("Export Model Snapshot"),
    /// so QC can be re-run on new CAD issues, on a build server, or on a machine without Revit.
    ///
    ///   CadQC --snapshot model.json --cad "D:\Project\CAD" [--out "D:\Project\QC"] [--settings qc-settings.json]
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var defaults = Arg(args, "--write-default-settings");
            if (defaults != null)
            {
                new QcSettings().Save(defaults);
                Console.WriteLine("Default settings written to " + defaults);
                return 0;
            }
            string snapPath = Arg(args, "--snapshot"), cad = Arg(args, "--cad"), outDir = Arg(args, "--out"), settingsPath = Arg(args, "--settings");
            if (snapPath == null || cad == null)
            {
                Console.WriteLine("Revit CAD Technical QC — command line\n");
                Console.WriteLine("  CadQC --snapshot <model_snapshot.json> --cad <CAD folder> [--out <folder>] [--settings <qc-settings.json>]\n");
                Console.WriteLine("  The snapshot is exported from Revit: CAD QC ribbon → Export Snapshot.");
                Console.WriteLine("  DWG files are converted automatically (ODA File Converter or AutoCAD must be installed).");
                return 2;
            }
            var settings = QcSettings.Load(settingsPath);
            settings.CadFolder = cad;
            if (outDir != null) settings.OutputFolder = outDir;
            var snap = JsonConvert.DeserializeObject<RevitSnapshot>(File.ReadAllText(snapPath));
            var engine = new QcEngine(settings, new Progress<QcProgress>(p => Console.WriteLine($"[{p.Percent,3}%] {p.Message}")));
            var report = engine.Run(snap);
            Console.WriteLine();
            foreach (var l in report.Log) Console.WriteLine("  " + l);
            Console.WriteLine();
            foreach (Severity s in Enum.GetValues(typeof(Severity))) Console.WriteLine($"  {s,-9} {report.Count(s)}");
            Console.WriteLine();
            foreach (var f in report.OutputFiles) Console.WriteLine("  → " + f);
            return report.Count(Severity.Critical) > 0 ? 1 : 0;
        }

        private static string Arg(string[] a, string name)
        {
            int i = Array.FindIndex(a, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        }
    }
}
