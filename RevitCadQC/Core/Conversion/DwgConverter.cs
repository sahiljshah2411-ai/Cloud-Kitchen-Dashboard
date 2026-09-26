using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RevitCadQC.Core.Conversion
{
    /// <summary>
    /// Converts DWG files to ASCII DXF automatically so the user never has to run DXFOUT by hand.
    /// Converter chain (first one that works wins):
    ///   0. ACadSharp, in-process (built in, nothing to install; DWG R14 to 2018+)
    ///   1. ODA File Converter (free, https://www.opendesign.com/guestfiles/oda_file_converter)
    ///   2. AutoCAD Core Console (accoreconsole.exe, ships with every AutoCAD / AutoCAD-based product)
    ///   3. LibreDWG dwg2dxf (if on PATH)
    /// Results are cached: a DWG is only re-converted when it is newer than its cached DXF.
    /// </summary>
    public sealed class DwgConverter
    {
        private readonly string _odaPath;
        private readonly string _acCorePath;
        private readonly string _dxfVersion;
        public string CacheFolder { get; }
        public int TimeoutSeconds { get; set; } = 300;
        public List<string> Log { get; } = new List<string>();

        public DwgConverter(string cacheFolder, string odaPath = null, string acCorePath = null, string dxfVersion = "ACAD2018")
        {
            CacheFolder = cacheFolder;
            Directory.CreateDirectory(cacheFolder);
            _odaPath = !string.IsNullOrWhiteSpace(odaPath) && File.Exists(odaPath) ? odaPath : FindOdaConverter();
            _acCorePath = !string.IsNullOrWhiteSpace(acCorePath) && File.Exists(acCorePath) ? acCorePath : FindAcCoreConsole();
            _dxfVersion = string.IsNullOrWhiteSpace(dxfVersion) ? "ACAD2018" : dxfVersion;
        }

        public string ConverterDescription =>
            "ACadSharp (built in)" + (_odaPath != null || _acCorePath != null ? ", fallback " : "") +
            (_odaPath != null ? "ODA File Converter: " + _odaPath :
            _acCorePath != null ? "AutoCAD Core Console: " + _acCorePath : "");

        public string ExternalConverterDescription =>
            _odaPath != null ? "ODA File Converter: " + _odaPath :
            _acCorePath != null ? "AutoCAD Core Console: " + _acCorePath :
            FindOnPath("dwg2dxf") != null ? "LibreDWG dwg2dxf" : "none";

        public bool HasConverter => true;

        public bool HasExternalConverter => _odaPath != null || _acCorePath != null || FindOnPath("dwg2dxf") != null;

        /// <summary>Returns a DXF path for any DWG or DXF input (DXF inputs are returned unchanged).</summary>
        public string EnsureDxf(string cadPath)
        {
            if (cadPath.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase)) return cadPath;
            if (!cadPath.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Unsupported CAD file: " + cadPath);

            string target = Path.Combine(CacheFolder, Path.GetFileNameWithoutExtension(cadPath) + "_" + ShortHash(Path.GetFullPath(cadPath)) + ".dxf");
            if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(cadPath) && new FileInfo(target).Length > 0)
            {
                Log.Add("Cached DXF used for " + Path.GetFileName(cadPath));
                return target;
            }

            var errors = new List<string>();
            try { if (ConvertWithAcadSharp(cadPath, target)) return target; }
            catch (Exception ex) { errors.Add("ACadSharp: " + ex.Message); }
            if (_odaPath != null)
            {
                try { if (ConvertWithOda(cadPath, target)) return target; }
                catch (Exception ex) { errors.Add("ODA: " + ex.Message); }
            }
            if (_acCorePath != null)
            {
                try { if (ConvertWithAcCore(cadPath, target)) return target; }
                catch (Exception ex) { errors.Add("AcCoreConsole: " + ex.Message); }
            }
            var libre = FindOnPath("dwg2dxf");
            if (libre != null)
            {
                try
                {
                    Run(libre, $"-y -o \"{target}\" \"{cadPath}\"", Path.GetDirectoryName(cadPath));
                    if (File.Exists(target)) return target;
                }
                catch (Exception ex) { errors.Add("dwg2dxf: " + ex.Message); }
            }

            if (!HasExternalConverter)
                throw new InvalidOperationException(
                    "The built-in DWG reader could not read this file (" + string.Join(" | ", errors) + ") and no external converter is installed. Install the free ODA File Converter (https://www.opendesign.com/guestfiles/oda_file_converter) " +
                    "or AutoCAD, or set 'OdaConverterPath' in the QC settings. DXF files in the folder are still checked.");
            throw new InvalidOperationException("DWG conversion failed for " + Path.GetFileName(cadPath) + ": " + string.Join(" | ", errors));
        }

        /// <summary>Reads the DWG with ACadSharp and writes it back as ASCII DXF for the QC reader.</summary>
        private bool ConvertWithAcadSharp(string dwg, string target)
        {
            var warnings = 0;
            ACadSharp.CadDocument doc;
            using (var fs = new FileStream(dwg, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                doc = ACadSharp.IO.DwgReader.Read(fs, new ACadSharp.IO.DwgReaderConfiguration { CrcCheck = false },
                    (s, e) => { if (e.NotificationType == ACadSharp.IO.NotificationType.Error) warnings++; });
            }
            if (doc == null) throw new InvalidOperationException("no document");
            var tmp = target + ".tmp";
            // write through our own stream: the path overload keeps the file handle open, which on Windows blocks the move below
            using (var os = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                ACadSharp.IO.DxfWriter.Write(os, doc, false, new ACadSharp.IO.DxfWriterConfiguration { WriteAllHeaderVariables = true }, null);
            if (!File.Exists(tmp) || new FileInfo(tmp).Length == 0) throw new InvalidOperationException("empty DXF");
            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
            Log.Add("Read DWG with ACadSharp: " + Path.GetFileName(dwg) + (warnings > 0 ? $" ({warnings} read warnings)" : ""));
            return true;
        }

        private bool ConvertWithOda(string dwg, string target)
        {
            // ODA works folder-to-folder, so stage the single file in a private temp folder.
            string stageIn = Path.Combine(Path.GetTempPath(), "cadqc_in_" + Guid.NewGuid().ToString("N"));
            string stageOut = Path.Combine(Path.GetTempPath(), "cadqc_out_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stageIn);
            Directory.CreateDirectory(stageOut);
            try
            {
                string staged = Path.Combine(stageIn, Path.GetFileName(dwg));
                File.Copy(dwg, staged, true);
                // ODAFileConverter <in> <out> <version> <DXF|DWG|DXB> <recurse 0/1> <audit 0/1> [filter]
                Run(_odaPath, $"\"{stageIn}\" \"{stageOut}\" {_dxfVersion} DXF 0 1 \"*.DWG\"", stageIn);
                var produced = Directory.GetFiles(stageOut, "*.dxf", SearchOption.AllDirectories).FirstOrDefault();
                if (produced == null) throw new InvalidOperationException("ODA produced no DXF (file may be corrupt or newer than the converter).");
                File.Copy(produced, target, true);
                Log.Add("Converted with ODA: " + Path.GetFileName(dwg));
                return true;
            }
            finally
            {
                TryDelete(stageIn);
                TryDelete(stageOut);
            }
        }

        private bool ConvertWithAcCore(string dwg, string target)
        {
            string year = new string(_dxfVersion.Where(char.IsDigit).ToArray());
            if (year.Length != 4) year = "2018";
            string scr = Path.Combine(Path.GetTempPath(), "cadqc_" + Guid.NewGuid().ToString("N") + ".scr");
            var sb = new StringBuilder();
            sb.AppendLine("_.FILEDIA 0");
            sb.AppendLine("_.CMDDIA 0");
            sb.AppendLine("_.DXFOUT");
            sb.AppendLine("\"" + target + "\"");
            sb.AppendLine("_V");
            sb.AppendLine(year);
            sb.AppendLine("16");
            sb.AppendLine("_.QUIT _Y");
            File.WriteAllText(scr, sb.ToString());
            try
            {
                Run(_acCorePath, $"/i \"{dwg}\" /s \"{scr}\" /l en-US", Path.GetDirectoryName(dwg));
                if (!File.Exists(target)) throw new InvalidOperationException("accoreconsole produced no DXF.");
                Log.Add("Converted with AutoCAD Core Console: " + Path.GetFileName(dwg));
                return true;
            }
            finally { TryDelete(scr); }
        }

        /// <summary>Converts a DXF (e.g. the QC markup) to DWG: ACadSharp first, ODA as fallback. Returns null when not possible.</summary>
        public string DxfToDwg(string dxf, string outFolder)
        {
            Directory.CreateDirectory(outFolder);
            var outDwg = Path.Combine(outFolder, Path.GetFileNameWithoutExtension(dxf) + ".dwg");
            try
            {
                ACadSharp.CadDocument src;
                using (var fs = new FileStream(dxf, FileMode.Open, FileAccess.Read, FileShare.Read))
                    src = ACadSharp.IO.DxfReader.Read(fs);
                // the markup DXF is minimal R12, which ACadSharp cannot write as DWG: copy it into a fresh (2018) document
                var doc = new ACadSharp.CadDocument();
                foreach (var l in src.Layers)
                    if (!doc.Layers.Contains(l.Name)) doc.Layers.Add(new ACadSharp.Tables.Layer(l.Name) { Color = l.Color });
                foreach (var e in src.Entities.ToList())
                {
                    var c = (ACadSharp.Entities.Entity)e.Clone();
                    c.Layer = doc.Layers[e.Layer.Name];
                    c.LineType = doc.LineTypes["ByLayer"];
                    doc.Entities.Add(c);
                }
                using (var os = new FileStream(outDwg, FileMode.Create, FileAccess.Write, FileShare.None))
                    ACadSharp.IO.DwgWriter.Write(os, doc);
                if (File.Exists(outDwg) && new FileInfo(outDwg).Length > 0) return outDwg;
            }
            catch (Exception ex) { Log.Add("Markup DWG via ACadSharp failed, trying ODA: " + ex.Message); }
            if (_odaPath == null) return null;
            string stageIn = Path.Combine(Path.GetTempPath(), "cadqc_min_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stageIn);
            try
            {
                File.Copy(dxf, Path.Combine(stageIn, Path.GetFileName(dxf)), true);
                Directory.CreateDirectory(outFolder);
                Run(_odaPath, $"\"{stageIn}\" \"{outFolder}\" {_dxfVersion} DWG 0 1 \"*.DXF\"", stageIn);
                var dwg = Path.Combine(outFolder, Path.GetFileNameWithoutExtension(dxf) + ".dwg");
                return File.Exists(dwg) ? dwg : null;
            }
            catch (Exception ex)
            {
                Log.Add("Markup DWG conversion failed: " + ex.Message);
                return null;
            }
            finally { TryDelete(stageIn); }
        }

        private void Run(string exe, string args, string workDir)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = workDir ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                if (p == null) throw new InvalidOperationException("Could not start " + exe);
                // drain output asynchronously so the child never blocks on a full pipe
                p.OutputDataReceived += (s, e) => { };
                p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Add(Path.GetFileName(exe) + ": " + e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(TimeoutSeconds * 1000))
                {
                    try { p.Kill(); } catch { /* ignored */ }
                    throw new TimeoutException(Path.GetFileName(exe) + " timed out after " + TimeoutSeconds + " s");
                }
            }
        }

        public static string FindOdaConverter()
        {
            var env = Environment.GetEnvironmentVariable("ODA_FILE_CONVERTER");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            foreach (var root in ProgramFolders())
            {
                var oda = Path.Combine(root, "ODA");
                if (!Directory.Exists(oda)) continue;
                var hit = SafeGetDirs(oda)
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, "ODAFileConverter.exe"))
                    .FirstOrDefault(File.Exists);
                if (hit != null) return hit;
            }
            return FindOnPath("ODAFileConverter");
        }

        public static string FindAcCoreConsole()
        {
            foreach (var root in ProgramFolders())
            {
                var ad = Path.Combine(root, "Autodesk");
                if (!Directory.Exists(ad)) continue;
                var hit = SafeGetDirs(ad)
                    .Where(d => Path.GetFileName(d).StartsWith("AutoCAD", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, "accoreconsole.exe"))
                    .FirstOrDefault(File.Exists);
                if (hit != null) return hit;
            }
            return null;
        }

        private static IEnumerable<string> ProgramFolders()
        {
            var list = new List<string>();
            foreach (var v in new[] { "ProgramFiles", "ProgramW6432", "ProgramFiles(x86)" })
            {
                var p = Environment.GetEnvironmentVariable(v);
                if (!string.IsNullOrEmpty(p) && !list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
            }
            if (list.Count == 0) list.Add(@"C:\Program Files");
            return list;
        }

        private static IEnumerable<string> SafeGetDirs(string path)
        {
            try { return Directory.GetDirectories(path); }
            catch { return Enumerable.Empty<string>(); }
        }

        private static string FindOnPath(string exe)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                foreach (var ext in new[] { ".exe", "" })
                {
                    try
                    {
                        var f = Path.Combine(dir.Trim(), exe + ext);
                        if (File.Exists(f)) return f;
                    }
                    catch { /* invalid path chars */ }
                }
            }
            return null;
        }

        private static string ShortHash(string s)
        {
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(s.ToLowerInvariant()));
                return BitConverter.ToString(h, 0, 4).Replace("-", "").ToLowerInvariant();
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch { /* best effort */ }
        }
    }
}
