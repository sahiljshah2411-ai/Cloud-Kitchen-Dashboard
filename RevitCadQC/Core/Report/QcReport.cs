using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RevitCadQC.Core.Alignment;
using RevitCadQC.Core.Geometry;
using Newtonsoft.Json;

namespace RevitCadQC.Core.Report
{
    public sealed class QcReport
    {
        public string Tool { get; set; } = "Revit CAD Technical QC";
        public string ToolVersion { get; set; } = typeof(QcReport).Assembly.GetName().Version?.ToString(3);
        public string ProjectName { get; set; }
        public string RevitFile { get; set; }
        public string CadFolder { get; set; }
        public DateTime RunUtc { get; set; } = DateTime.UtcNow;
        public double DurationSeconds { get; set; }
        public List<FloorResult> Floors { get; set; } = new List<FloorResult>();
        public List<QcIssue> Issues { get; set; } = new List<QcIssue>();
        public List<string> Log { get; set; } = new List<string>();
        public List<string> OutputFiles { get; set; } = new List<string>();

        public int Count(Severity s) => Issues.Count(i => i.Severity == s && i.Status != IssueStatus.Resolved);
        public int OpenCount => Issues.Count(i => i.Status != IssueStatus.Resolved && i.Status != IssueStatus.Accepted);

        public void SaveJson(string path)
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented,
                new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
        }

        public static QcReport LoadJson(string path)
        {
            if (!File.Exists(path)) return null;
            return JsonConvert.DeserializeObject<QcReport>(File.ReadAllText(path));
        }
    }

    public sealed class FloorResult
    {
        public string Floor { get; set; }
        public string FloorCode { get; set; }
        public string LevelName { get; set; }
        public string CadFile { get; set; }
        public string RegionTitle { get; set; }
        public string UnitsNote { get; set; }
        /// <summary>Drawing units per millimetre factor (mm per drawing unit), needed to write CAD markup back in drawing units.</summary>
        public double CadUnitToMm { get; set; } = 1;
        public AlignmentResult Alignment { get; set; }
        public Dictionary<string, int> CadCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> RevitCounts { get; set; } = new Dictionary<string, int>();
        public List<string> Notes { get; set; } = new List<string>();
        public int IssueCount { get; set; }

        /// <summary>Plan geometry for the HTML overlay and CAD markup (Revit frame, mm). Not stored in the JSON.</summary>
        [JsonIgnore]
        public FloorOverlay Overlay { get; set; }
    }

    public sealed class FloorOverlay
    {
        /// <summary>CAD linework (wall, door, window, column layers) transformed into the Revit frame.</summary>
        public List<Seg2> CadLines { get; set; } = new List<Seg2>();
        /// <summary>Revit wall footprints (4 corners each), Revit frame.</summary>
        public List<List<Vec2>> RevitWalls { get; set; } = new List<List<Vec2>>();
        public List<List<Vec2>> RevitColumns { get; set; } = new List<List<Vec2>>();
        public List<(Vec2 p, double w, string kind)> RevitOpenings { get; set; } = new List<(Vec2, double, string)>();
        public List<Seg2> RevitGrids { get; set; } = new List<Seg2>();
        public Box2 Bounds { get; set; } = Box2.Empty;
    }
}
