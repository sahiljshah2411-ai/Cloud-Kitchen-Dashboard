using System;
using System.IO;
using System.Linq;
using CadQC.Core.Dxf;
using CadQC.Core.Engine;
using CadQC.Core.Extraction;
using CadQC.Core.Geometry;
using CadQC.Core.Model;
using CadQC.Core.Report;
using CadQC.Core.Settings;
using Xunit;
using Xunit.Abstractions;

namespace CadQC.Tests
{
    public class QcTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _dir;

        public QcTests(ITestOutputHelper output)
        {
            _out = output;
            _dir = Path.Combine(Path.GetTempPath(), "cadqc_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private QcReport RunEngine(double unitScale = 1.0, Action<QcSettings> tweak = null)
        {
            File.WriteAllText(Path.Combine(_dir, "2ND FLOOR PLAN.dxf"), TestPlan.BuildDxf(unitScale));
            var s = new QcSettings { CadFolder = _dir, OutputFolder = Path.Combine(_dir, "out") };
            tweak?.Invoke(s);
            var report = new QcEngine(s).Run(TestPlan.BuildSnapshot());
            foreach (var l in report.Log) _out.WriteLine(l);
            foreach (var i in report.Issues) _out.WriteLine(i.ToString());
            return report;
        }

        [Fact]
        public void Reader_explodes_blocks_and_reads_units()
        {
            var dwg = new DxfReader().ReadText(TestPlan.BuildDxf());
            Assert.Equal(4, dwg.InsUnits);
            Assert.Equal(2, dwg.Inserts.Count);
            Assert.Contains(dwg.Arcs, a => Math.Abs(a.Radius - 900) < 1e-6 && a.Layer == "A-DOOR");
            Assert.Single(dwg.Dimensions);
            Assert.Contains(dwg.Texts, t => t.Text == "230 THK. BRICK WALL");
        }

        [Fact]
        public void Walls_are_recovered_with_thickness()
        {
            var dwg = new DxfReader().ReadText(TestPlan.BuildDxf());
            var data = new CadExtractor(new QcSettings()).Extract(dwg);
            foreach (var w in data.Walls) _out.WriteLine(w + " note=" + w.AnnotatedThickness);
            Assert.Contains(data.Walls, w => Math.Abs(w.Thickness - 230) < 0.5 && Math.Abs(w.Centerline.Mid.Y - 4000) < 1 && w.AnnotatedThickness == 230);
            Assert.Contains(data.Walls, w => Math.Abs(w.Thickness - 115) < 0.5 && Math.Abs(w.Centerline.Mid.Y - 6000) < 1);
            Assert.True(data.Walls.Count(w => Math.Abs(w.Thickness - 115) < 0.5 && Math.Abs(w.Centerline.Mid.X - 5000) < 1) == 2, "wall A split by the door gap");
            Assert.Equal(2, data.Columns.Count);
            Assert.Equal(4, data.Grids.Count(g => g.Name != null));
            var door = Assert.Single(data.Openings, o => o.Kind == OpeningKind.Door);
            Assert.Equal(900, door.Width, 0);
            Assert.Equal("D1", door.Tag);
            var win = Assert.Single(data.Openings, o => o.Kind == OpeningKind.Window);
            Assert.Equal(1200, win.Width, 0);
            Assert.Contains(data.Rooms, r => r.Name == "BEDROOM" && r.SizeA == 4700);
        }

        [Fact]
        public void Full_run_finds_every_planted_difference()
        {
            var r = RunEngine();
            var f = Assert.Single(r.Floors);
            Assert.Equal("Second Floor", f.LevelName);
            Assert.True(f.Alignment.Reliable, f.Alignment.ToString());
            Assert.True(Math.Abs(f.Alignment.Transform.Rotation - 30 * GeoMath.Deg) < 0.01 * GeoMath.Deg, f.Alignment.ToString());

            var open = r.Issues.Where(i => i.Status != IssueStatus.Resolved).ToList();
            Assert.Contains(open, i => i.Category == IssueCategory.Wall && i.IssueType == IssueType.ThicknessMismatch && i.Title.Contains("230") && i.Title.Contains("200"));
            Assert.Contains(open, i => i.Category == IssueCategory.Wall && i.IssueType == IssueType.MissingInRevit && Near(i, 7413, 6000, 300));
            Assert.Contains(open, i => i.Category == IssueCategory.Wall && i.IssueType == IssueType.ExtraInRevit && i.Severity == Severity.Major && Near(i, 7500, 1600, 1500));
            Assert.Contains(open, i => i.Category == IssueCategory.Door && i.IssueType == IssueType.WidthMismatch);
            Assert.Contains(open, i => i.Category == IssueCategory.Window && i.IssueType == IssueType.MissingInRevit);
            Assert.Contains(open, i => i.Category == IssueCategory.Column && i.IssueType == IssueType.PositionOffset && Math.Abs(i.Delta.Value - 60) < 1);
            Assert.Contains(open, i => i.Category == IssueCategory.Room && i.IssueType == IssueType.NameMismatch);
            Assert.Contains(open, i => i.Category == IssueCategory.Dimension && i.IssueType == IssueType.DimensionMismatch && Math.Abs(i.Delta.Value + 30) < 1);
            Assert.Contains(open, i => i.Category == IssueCategory.ModelHygiene && i.IssueType == IssueType.TypeNameMismatch);

            // no false alarms on the correctly modelled exterior walls or grids
            Assert.DoesNotContain(open, i => i.Category == IssueCategory.Wall && i.IssueType == IssueType.MissingInRevit && !Near(i, 7413, 6000, 300));
            Assert.DoesNotContain(open, i => i.Category == IssueCategory.Grid);
            Assert.DoesNotContain(open, i => i.Category == IssueCategory.Wall && i.IssueType == IssueType.PositionOffset);
            Assert.DoesNotContain(open, i => i.Category == IssueCategory.Alignment);

            // outputs
            foreach (var p in new[] { "QC_Report.html", "QC_Issues.csv", "QC_Issues.xlsx", "qc_report.json", "CAD_Markup/2ND FLOOR PLAN_QC_markup.dxf", "CAD_Markup/2ND FLOOR PLAN_QC_markup.scr" })
                Assert.True(File.Exists(Path.Combine(_dir, "out", p)), p);
            var sampleOut = Environment.GetEnvironmentVariable("CADQC_SAMPLE_OUT");
            if (!string.IsNullOrEmpty(sampleOut))
            {
                foreach (var file in Directory.GetFiles(Path.Combine(_dir, "out"), "*", SearchOption.AllDirectories).Where(x => !x.Contains("history") && !x.Contains(".dxf-cache")))
                {
                    var dest = Path.Combine(sampleOut, Path.GetRelativePath(Path.Combine(_dir, "out"), file));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    File.Copy(file, dest, true);
                }
                File.Copy(Path.Combine(_dir, "2ND FLOOR PLAN.dxf"), Path.Combine(sampleOut, "input_2ND FLOOR PLAN.dxf"), true);
                File.WriteAllText(Path.Combine(sampleOut, "input_model_snapshot.json"), Newtonsoft.Json.JsonConvert.SerializeObject(TestPlan.BuildSnapshot(), Newtonsoft.Json.Formatting.Indented));
            }
            // markup DXF must be readable again and sit at the CAD location of the issues
            var markup = new DxfReader().Read(Path.Combine(_dir, "out", "CAD_Markup", "2ND FLOOR PLAN_QC_markup.dxf"));
            Assert.Contains(markup.Curves, c => c.Layer == "QC_CRITICAL");
        }

        [Fact]
        public void Second_run_tracks_status()
        {
            RunEngine();
            var r2 = RunEngine();
            Assert.DoesNotContain(r2.Issues, i => i.Status == IssueStatus.New);
            Assert.Contains(r2.Issues, i => i.Status == IssueStatus.Open);
        }

        [Fact]
        public void Unitless_drawing_in_metres_is_detected()
        {
            var r = RunEngine(unitScale: 1000);
            var f = Assert.Single(r.Floors);
            Assert.Contains("1000", f.UnitsNote);
            Assert.Contains(r.Issues, i => i.IssueType == IssueType.ThicknessMismatch);
        }

        [Theory]
        [InlineData("2ND FLOOR PLAN", 2)]
        [InlineData("SECOND FLOOR", 2)]
        [InlineData("Level 02", 2)]
        [InlineData("GF", 0)]
        [InlineData("Ground Floor Plan", 0)]
        [InlineData("FIRST FLOOR", 1)]
        [InlineData("B1 Parking", -1)]
        [InlineData("TERRACE FLOOR PLAN", 1000)]
        [InlineData("L-3", 3)]
        public void Floor_names(string name, int expected)
        {
            Assert.Equal(expected, TextParsing.ParseFloors(name).First());
        }

        [Fact]
        public void Typical_floor_range()
        {
            Assert.Equal(new[] { 3, 4, 5, 6, 7 }, TextParsing.ParseFloors("TYPICAL FLOOR PLAN (3RD TO 7TH)"));
        }

        [Theory]
        [InlineData("230 THK", 230)]
        [InlineData("230MM THK. BRICK WALL", 230)]
        [InlineData("115 THK PARTITION", 115)]
        [InlineData("9\" BRICK WALL", 230)]
        [InlineData("4.5\" THK WALL", 115)]
        [InlineData("200 AAC BLOCK WALL", 200)]
        public void Thickness_notes(string text, double mm)
        {
            Assert.Equal(mm, TextParsing.ParseThicknessNote(text));
        }

        [Theory]
        [InlineData("3000 X 3600", 3000, 3600)]
        [InlineData("10'0\" X 12'6\"", 3048, 3810)]
        [InlineData("3.0 x 3.6", 3000, 3600)]
        public void Room_sizes(string text, double a, double b)
        {
            Assert.True(TextParsing.TryParseSize(text, out var x, out var y));
            Assert.Equal(a, x, 0);
            Assert.Equal(b, y, 0);
        }

        [Fact]
        public void Level_1_is_ground_in_generic_templates()
        {
            var m = new LevelMatcher(new[]
            {
                new QcLevel { Name = "Level 1", ElevationMm = 0 },
                new QcLevel { Name = "Level 2", ElevationMm = 3000 },
                new QcLevel { Name = "Level 3", ElevationMm = 6000 }
            });
            Assert.Equal("Level 1", m.Find(0).Name);
            Assert.Equal("Level 3", m.Find(2).Name);
        }

        private static bool Near(QcIssue i, double cadX, double cadY, double tol)
        {
            if (!i.RevitLocation.HasValue) return false;
            var p = TestPlan.CadToRevit.Inverse().Apply(i.RevitLocation.Value);
            return p.DistanceTo(new Vec2(cadX, cadY)) <= tol;
        }
    }
}
