using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CadQC.Core.Conversion;
using CadQC.Core.Dxf;
using CadQC.Core.Settings;

namespace CadQC.Core.Engine
{
    public sealed class ScannedLayer
    {
        public string Name { get; set; }
        public int Entities { get; set; }
        public LayerCategory RuleCategory { get; set; }
        public LayerCategory ProfileCategory { get; set; }
        public List<string> Files { get; set; } = new List<string>();
    }

    public sealed class ScannedFile
    {
        public string Path { get; set; }
        public CadLayerProfile Profile { get; set; }
        public double Coverage { get; set; }
        public string Error { get; set; }
        public Dictionary<string, int> LayerCounts { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Quick pass over the CAD folder for the layer review grid: which layers exist, how much is on each,
    /// what the rules think they are and whether a saved consultant profile already covers the drawing.
    /// </summary>
    public static class LayerScanner
    {
        public static List<ScannedFile> Scan(QcSettings s, IProgress<QcProgress> progress = null, CancellationToken ct = default)
        {
            var res = new List<ScannedFile>();
            if (string.IsNullOrWhiteSpace(s.CadFolder) || !Directory.Exists(s.CadFolder)) return res;
            var outDir = !string.IsNullOrWhiteSpace(s.OutputFolder) ? s.OutputFolder : Path.Combine(s.CadFolder, "_RevitCadQC");
            var conv = new DwgConverter(Path.Combine(outDir, ".dxf-cache"), s.OdaConverterPath, s.AcCoreConsolePath, s.DxfOutputVersion);
            var files = Directory.GetFiles(s.CadFolder, "*.*", s.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(f => (f.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
                            && !f.Contains("_RevitCadQC") && !Path.GetFileName(f).Contains("_QC_markup"))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new QcProgress { Percent = 100 * i / Math.Max(1, files.Count), Message = "Reading layers of " + Path.GetFileName(files[i]) + "…" });
                var sf = new ScannedFile { Path = files[i] };
                try
                {
                    var dwg = DxfReader.Load(conv.EnsureDxf(files[i]), s.SkipFrozenAndOffLayers);
                    foreach (var l in dwg.Curves.Select(c => c.Layer).Concat(dwg.Texts.Select(t => t.Layer)).Concat(dwg.Inserts.Select(x => x.Layer)).Concat(dwg.Dimensions.Select(x => x.Layer)))
                    {
                        if (string.IsNullOrEmpty(l)) continue;
                        sf.LayerCounts.TryGetValue(l, out int n);
                        sf.LayerCounts[l] = n + 1;
                    }
                    sf.Profile = CadProfileStore.BestMatch(sf.LayerCounts.Keys, out double cov);
                    sf.Coverage = cov;
                }
                catch (Exception ex) { sf.Error = ex.Message; }
                res.Add(sf);
            }
            progress?.Report(new QcProgress { Percent = 100, Message = "Layers read." });
            return res;
        }

        /// <summary>All layers of the files that still need a review, merged, with the rule / profile guess.</summary>
        public static List<ScannedLayer> Merge(IEnumerable<ScannedFile> files, LayerClassifier rules)
        {
            var map = new Dictionary<string, ScannedLayer>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                foreach (var kv in f.LayerCounts)
                {
                    if (!map.TryGetValue(kv.Key, out var sl))
                    {
                        sl = new ScannedLayer { Name = kv.Key, RuleCategory = rules.ClassifyByRules(kv.Key) };
                        if (f.Profile != null && f.Profile.Layers.TryGetValue(kv.Key, out var pc) && Enum.TryParse(pc, true, out LayerCategory c))
                            sl.ProfileCategory = c;
                        map[kv.Key] = sl;
                    }
                    sl.Entities += kv.Value;
                    sl.Files.Add(System.IO.Path.GetFileName(f.Path));
                }
            }
            return map.Values.OrderBy(l => l.RuleCategory == LayerCategory.Unknown ? 1 : 0).ThenByDescending(l => l.Entities).ToList();
        }
    }
}
