namespace RevitCadQC.Core
{
    /// <summary>
    /// Version shown on the Version button and written to the team package's version.txt by
    /// RUN_4_MAKE_TEAM_PACKAGE. Bump Version + UpdatedOn and add a note line for every team release.
    /// </summary>
    public static class ToolVersion
    {
        public const string Version = "1.1.0";
        public const string UpdatedOn = "2026-09-26";

        /// <summary>Change notes, one string per line starting with a dash (parsed by 4_MAKE_TEAM_PACKAGE.ps1).</summary>
        public static readonly string[] Notes =
        {
            "- DWG read directly (ACadSharp 3.6.35), no converter needed",
            "- Consultant layer profiles + CadLayerDictionary.txt",
            "- Central office rules in CadQcConfig.txt",
            "- Check for Update from the team package folder",
        };

        public static string Display => "Revit CAD QC " + Version + " (" + UpdatedOn + ")";

        /// <summary>True when <paramref name="remote"/> is a higher dotted version than <paramref name="local"/>.</summary>
        public static bool IsNewer(string remote, string local)
        {
            if (!System.Version.TryParse(Pad(remote), out var r) || !System.Version.TryParse(Pad(local), out var l)) return false;
            return r > l;
        }

        private static string Pad(string v)
        {
            v = (v ?? "").Trim();
            int dots = v.Split('.').Length - 1;
            for (int i = dots; i < 1; i++) v += ".0";
            return v;
        }
    }
}
