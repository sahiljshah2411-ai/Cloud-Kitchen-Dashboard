using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using RevitCadQC.Core.Settings;

namespace RevitCadQC.Core
{
    /// <summary>
    /// Team update from the shared package folder (made by RUN_4_MAKE_TEAM_PACKAGE):
    ///   version.txt   line 1 = version, line 2 = date, optional "FORCE" (silent auto-update at Revit start),
    ///                 optional "NEWPATH=&lt;folder&gt;" (package moved), then "- note" lines
    ///   R2022\ R2025\ …  DLLs per Revit version
    /// Revit locks the loaded DLL but allows RENAMING it, so the running copy is renamed to *.old and the new one
    /// copied beside it; *.old files are deleted at the next Revit start.
    /// </summary>
    public static class UpdateCore
    {
        public sealed class RemoteInfo
        {
            public string Version { get; set; }
            public string Date { get; set; }
            public bool Force { get; set; }
            public string NewPath { get; set; }
            public List<string> Notes { get; } = new List<string>();
        }

        public static string AddinsFolder(string revitYear) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", revitYear);

        public static string InstallFolder(string revitYear) => Path.Combine(AddinsFolder(revitYear), "RevitCadQC");

        public static RemoteInfo ReadRemote(string pkg)
        {
            if (string.IsNullOrEmpty(pkg)) return null;
            var vf = Path.Combine(pkg, "version.txt");
            if (!File.Exists(vf)) return null;
            string[] lines;
            // FileShare.ReadWrite: never blocked by (or blocking) someone editing the file in Notepad
            using (var fs = new FileStream(vf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var rd = new StreamReader(fs))
                lines = rd.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();
            if (lines.Length == 0) return null;
            var info = new RemoteInfo { Version = lines[0], Date = lines.Length > 1 && !lines[1].StartsWith("-") ? lines[1] : "" };
            foreach (var l in lines.Skip(1))
            {
                if (l.Equals("FORCE", StringComparison.OrdinalIgnoreCase)) info.Force = true;
                else if (l.StartsWith("NEWPATH=", StringComparison.OrdinalIgnoreCase)) info.NewPath = l.Substring(8).Trim();
                else if (l.StartsWith("-")) info.Notes.Add(l);
            }
            return info;
        }

        /// <summary>Follows NEWPATH= redirects (max 3) and remembers the final package folder.</summary>
        public static string ResolvePackage(string pkg)
        {
            for (int i = 0; i < 3 && pkg != null; i++)
            {
                var info = ReadRemote(pkg);
                if (info?.NewPath == null || !Directory.Exists(info.NewPath) || string.Equals(info.NewPath, pkg, StringComparison.OrdinalIgnoreCase)) break;
                pkg = info.NewPath;
                PackagePath.Set(pkg);
            }
            return pkg;
        }

        /// <summary>Copies the package build for this Revit version over the installed one. Returns files copied (0 = failed).</summary>
        public static int CopyLatest(string pkg, string revitYear, out string error)
        {
            error = null;
            var src = Path.Combine(pkg, "R" + revitYear);
            if (!Directory.Exists(src)) { error = "The package has no build for Revit " + revitYear + " (" + src + ")."; return 0; }
            var dst = InstallFolder(revitYear);
            int n = 0;
            try
            {
                Directory.CreateDirectory(dst);
                foreach (var f in Directory.GetFiles(src))
                {
                    var name = Path.GetFileName(f);
                    var target = Path.Combine(dst, name);
                    if (File.Exists(target) && Hash(target) == Hash(f)) continue;
                    if (File.Exists(target))
                    {
                        try { File.Delete(target); }
                        catch (Exception)
                        {
                            // loaded by Revit: rename it out of the way
                            var old = target + ".old";
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(target, old);
                        }
                    }
                    File.Copy(f, target, true);
                    if (Hash(target) != Hash(f)) { error = "Copy check failed for " + name; return 0; }
                    n++;
                }
                var addin = Path.Combine(pkg, "RevitCadQC.addin");
                if (File.Exists(addin)) File.Copy(addin, Path.Combine(AddinsFolder(revitYear), "RevitCadQC.addin"), true);
                n += SyncShared(pkg);
                return Math.Max(n, 1);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return 0;
            }
        }

        /// <summary>Adds consultant profiles from the package that this user does not have yet (never overwrites).</summary>
        public static int SyncShared(string pkg)
        {
            int n = 0;
            try
            {
                var pp = Path.Combine(pkg, "CadProfiles");
                if (!Directory.Exists(pp)) return 0;
                foreach (var f in Directory.GetFiles(pp, "*.json"))
                {
                    var t = Path.Combine(CadProfileStore.Folder, Path.GetFileName(f));
                    if (File.Exists(t)) continue;
                    File.Copy(f, t);
                    n++;
                }
            }
            catch { }
            return n;
        }

        /// <summary>Deletes *.old copies left by a previous update (they are no longer loaded).</summary>
        public static void CleanupOld(string revitYear)
        {
            try
            {
                var d = InstallFolder(revitYear);
                if (!Directory.Exists(d)) return;
                foreach (var f in Directory.GetFiles(d, "*.old"))
                    try { File.Delete(f); } catch { }
            }
            catch { }
        }

        private static string Hash(string f)
        {
            using (var sha = SHA256.Create())
            using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return BitConverter.ToString(sha.ComputeHash(s));
        }
    }
}
