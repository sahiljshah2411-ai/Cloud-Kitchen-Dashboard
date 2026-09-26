using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CadQC.Core.Geometry;
using CadQC.Core.Model;
using CadQC.Core.Report;
using CadQC.Core.Settings;

namespace CadQC.Core.Compare
{
    /// <summary>Everything a comparer needs for one floor: CAD data already in the Revit frame, the Revit elements, and issue bookkeeping.</summary>
    public sealed class FloorContext
    {
        public string FloorLabel { get; set; }
        public string FloorCode { get; set; }
        public string LevelName { get; set; }
        public string CadFile { get; set; }
        public QcSettings Settings { get; set; }

        /// <summary>CAD → Revit (both in mm).</summary>
        public Similarity2 CadToRevit { get; set; } = Similarity2.Identity;

        public CadFloorData Cad { get; set; }
        public List<QcWall> RevitWalls { get; set; } = new List<QcWall>();
        public List<QcOpening> RevitOpenings { get; set; } = new List<QcOpening>();
        public List<QcColumn> RevitColumns { get; set; } = new List<QcColumn>();
        public List<QcRoom> RevitRooms { get; set; } = new List<QcRoom>();
        public List<QcGrid> RevitGrids { get; set; } = new List<QcGrid>();

        /// <summary>CAD wall id → best matching Revit wall, filled by the wall comparer, used by the dimension check.</summary>
        public Dictionary<string, QcWall> WallMatches { get; } = new Dictionary<string, QcWall>();

        public List<QcIssue> Issues { get; } = new List<QcIssue>();
        private readonly Dictionary<string, int> _counters = new Dictionary<string, int>();

        private Similarity2 _inv;
        public Vec2 ToCad(Vec2 revit) => (_inv ?? (_inv = CadToRevit.Inverse())).Apply(revit);

        public QcIssue Add(string category, string type, Severity sev, string title, Vec2? revitLoc,
                           string description = null, string cadValue = null, string revitValue = null, double? delta = null,
                           IEnumerable<string> revitIds = null, IEnumerable<string> cadHandles = null, string cadLayer = null,
                           List<Vec2> revitPath = null, double markerRadius = 600)
        {
            string code = CategoryCode(category);
            _counters.TryGetValue(code, out int n);
            _counters[code] = ++n;
            var issue = new QcIssue
            {
                Id = $"{FloorCode}-{code}-{n:000}",
                Floor = FloorLabel,
                LevelName = LevelName,
                Category = category,
                IssueType = type,
                Severity = sev,
                Title = title,
                Description = description ?? title,
                CadValue = cadValue,
                RevitValue = revitValue,
                Delta = delta.HasValue ? Math.Round(delta.Value, 1) : (double?)null,
                RevitLocation = revitLoc,
                CadLocation = revitLoc.HasValue ? ToCad(revitLoc.Value) : (Vec2?)null,
                RevitPath = revitPath,
                CadPath = revitPath?.Select(ToCad).ToList(),
                RevitElementIds = revitIds?.Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList() ?? new List<string>(),
                CadHandles = cadHandles?.Where(x => !string.IsNullOrEmpty(x)).Distinct().Take(20).ToList() ?? new List<string>(),
                CadLayer = cadLayer,
                CadFile = CadFile,
                MarkerRadius = markerRadius
            };
            var key = revitLoc.HasValue
                ? string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3:0}|{4:0}", LevelName, category, type, Math.Round(revitLoc.Value.X / 200), Math.Round(revitLoc.Value.Y / 200))
                : $"{LevelName}|{category}|{type}|{title}";
            issue.Key = key;
            Issues.Add(issue);
            return issue;
        }

        public static string CategoryCode(string category)
        {
            switch (category)
            {
                case IssueCategory.Wall: return "WAL";
                case IssueCategory.Door: return "DR";
                case IssueCategory.Window: return "WIN";
                case IssueCategory.Column: return "COL";
                case IssueCategory.Room: return "RM";
                case IssueCategory.Grid: return "GRD";
                case IssueCategory.Dimension: return "DIM";
                case IssueCategory.Alignment: return "ALN";
                case IssueCategory.CadDrafting: return "CAD";
                case IssueCategory.ModelHygiene: return "MDL";
                default: return "GEN";
            }
        }

        public static string Mm(double v) => v.ToString("0", CultureInfo.InvariantCulture) + " mm";
        public static string Mm1(double v) => v.ToString("0.#", CultureInfo.InvariantCulture) + " mm";
    }
}
