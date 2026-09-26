using System;
using System.IO;
using System.Linq;
using RevitCadQC.Core.Dxf;
using RevitCadQC.Core.Engine;
using RevitCadQC.Core.Extraction;
using RevitCadQC.Core.Geometry;
using RevitCadQC.Core.Model;
using RevitCadQC.Core.Report;
using RevitCadQC.Core.Settings;
using Xunit;
using Xunit.Abstractions;

namespace RevitCadQC.Tests
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
            foreach (var p in new[] { "QC_Report.html", "QC_Issues.csv", "QC_Issues.xlsx", "qc_report.json", "CAD_Markup/2ND FLOOR PLAN_QC_markup.dxf", "CAD_Markup/2ND FLOOR PLAN_QC_markup.dwg", "CAD_Markup/2ND FLOOR PLAN_QC_markup.scr" })
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
            // and the DWG copy opens as a real DWG with the QC layers
            ACadSharp.CadDocument dwgDoc;
            using (var ms = File.OpenRead(Path.Combine(_dir, "out", "CAD_Markup", "2ND FLOOR PLAN_QC_markup.dwg"))) dwgDoc = ACadSharp.IO.DwgReader.Read(ms);
            Assert.Contains(dwgDoc.Layers, l => l.Name == "QC_CRITICAL");
            Assert.Contains(dwgDoc.Entities, e => e.Layer.Name == "QC_CRITICAL");
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
        public void Dwg_is_read_directly_without_external_converter()
        {
            // make a real DWG from the test plan with ACadSharp, then QC the DWG only
            var dxf = Path.Combine(Path.GetTempPath(), "cadqc_src_" + Guid.NewGuid().ToString("N") + ".dxf");
            File.WriteAllText(dxf, TestPlan.BuildDxf());
            var doc = ACadSharp.IO.DxfReader.Read(dxf);
            doc.CreateDefaults(); // the hand-written test DXF has no OBJECTS section; a real DWG always does
            using (var os = File.Create(Path.Combine(_dir, "2ND FLOOR PLAN.dwg"))) ACadSharp.IO.DwgWriter.Write(os, doc);
            File.Delete(dxf);

            var s = new QcSettings { CadFolder = _dir, OutputFolder = Path.Combine(_dir, "out"), OdaConverterPath = "", AcCoreConsolePath = "" };
            var r = new QcEngine(s).Run(TestPlan.BuildSnapshot());
            foreach (var l in r.Log) _out.WriteLine(l);
            foreach (var i in r.Issues) _out.WriteLine(i.ToString());
            Assert.Contains(r.Log, l => l.Contains("ACadSharp"));
            var f = Assert.Single(r.Floors);
            Assert.True(f.Alignment.Reliable, f.Alignment.ToString());
            Assert.Contains(r.Issues, i => i.IssueType == IssueType.ThicknessMismatch && i.Title.Contains("230") && i.Title.Contains("200"));
            Assert.Contains(r.Issues, i => i.Category == IssueCategory.Wall && i.IssueType == IssueType.MissingInRevit);
            Assert.Contains(r.Issues, i => i.Category == IssueCategory.Door && i.IssueType == IssueType.WidthMismatch);
            Assert.Contains(r.Issues, i => i.Category == IssueCategory.Window && i.IssueType == IssueType.MissingInRevit);
            Assert.Contains(r.Issues, i => i.Category == IssueCategory.Column && i.IssueType == IssueType.PositionOffset);
        }

        [Theory]
        [InlineData("A-WALL", LayerCategory.Wall)]
        [InlineData("A-WALL-PATT", LayerCategory.Ignore)]
        [InlineData("A-DOOR", LayerCategory.Door)]
        [InlineData("A-GLAZ", LayerCategory.Window)]
        [InlineData("S-COLS", LayerCategory.Column)]
        [InlineData("S-GRID", LayerCategory.Grid)]
        [InlineData("A-ANNO-DIMS", LayerCategory.Dimension)]
        [InlineData("A-AREA-IDEN", LayerCategory.Room)]
        [InlineData("FURNITURE", LayerCategory.Ignore)]
        [InlineData("230 BRICK WALL", LayerCategory.Wall)]
        [InlineData("SOMETHING ODD", LayerCategory.Unknown)]
        public void Dictionary_file_classifies_layers(string layer, LayerCategory expected)
        {
            var c = new LayerClassifier();
            Assert.True(c.LoadDictionary(Path.Combine(AppContext.BaseDirectory, "config", "CadLayerDictionary.txt")));
            Assert.Equal(expected, c.Classify(layer));
        }

        [Fact]
        public void Consultant_profile_maps_unusual_layer_names()
        {
            // a consultant who draws walls on "Z-01" and columns on "Z-02": no rule knows these names
            var dxf = TestPlan.BuildDxf().Replace("\nA-WALL\n", "\nZ-01\n").Replace("\nS-COLS\n", "\nZ-02\n");
            File.WriteAllText(Path.Combine(_dir, "2ND FLOOR PLAN.dxf"), dxf);
            var profDir = Path.Combine(_dir, "profiles");
            Environment.SetEnvironmentVariable("CADQC_PROFILE_DIR", profDir);
            try
            {
                var s = new QcSettings { CadFolder = _dir, OutputFolder = Path.Combine(_dir, "out") };
                var scan = LayerScanner.Scan(s);
                var layers = LayerScanner.Merge(scan, s.Layers);
                Assert.Contains(layers, l => l.Name == "Z-01" && l.RuleCategory == LayerCategory.Unknown);

                // what the review grid saves after the user picks Wall / Column
                var p = new CadLayerProfile { Name = "Consultant Z" };
                foreach (var l in layers) p.Layers[l.Name] = l.RuleCategory.ToString();
                p.Layers["Z-01"] = "Wall";
                p.Layers["Z-02"] = "Column";
                CadProfileStore.Save(p);

                var r = new QcEngine(new QcSettings { CadFolder = _dir, OutputFolder = Path.Combine(_dir, "out") }).Run(TestPlan.BuildSnapshot());
                foreach (var l in r.Log) _out.WriteLine(l);
                Assert.Contains(r.Log, l => l.Contains("Consultant Z"));
                Assert.Contains(r.Issues, i => i.IssueType == IssueType.ThicknessMismatch);
                Assert.Contains(r.Issues, i => i.Category == IssueCategory.Column && i.IssueType == IssueType.PositionOffset);
            }
            finally { Environment.SetEnvironmentVariable("CADQC_PROFILE_DIR", null); }
        }

        [Fact]
        public void Central_config_sets_defaults_and_enforced_values()
        {
            var cfg = Path.Combine(_dir, "CadQcConfig.txt");
            File.WriteAllText(cfg, "WallThicknessTol=8\nTHICKNESS_EQUIV=230|254,250\nROOM_KEYWORD=MUMTY\n!CheckGrids=false\nNoSuchSetting=1\nWallPositionTolMajor=abc\n");
            var s = new QcSettings();
            var log = CadQcConfig.Apply(s, false, cfg);
            Assert.Equal(8, s.WallThicknessTol);
            Assert.False(s.CheckGrids);
            Assert.Contains(s.ThicknessEquivalents, e => e.Cad == 230 && e.Revit.Contains(254) && e.Revit.Contains(250));
            Assert.Contains("MUMTY", s.RoomKeywords);
            Assert.Contains(log, l => l.Contains("NoSuchSetting"));
            Assert.Contains(log, l => l.Contains("WallPositionTolMajor"));

            // a project file changed the tolerance and re-enabled grids: only the enforced line wins back
            s.WallThicknessTol = 3; s.CheckGrids = true;
            CadQcConfig.Apply(s, true, cfg);
            Assert.Equal(3, s.WallThicknessTol);
            Assert.False(s.CheckGrids);
        }

        [Fact]
        public void Update_package_version_file_is_read()
        {
            File.WriteAllText(Path.Combine(_dir, "version.txt"), "1.4.2\r\n2026-10-01\r\nFORCE\r\n- better walls\r\n- faster DWG\r\n");
            var r = RevitCadQC.Core.UpdateCore.ReadRemote(_dir);
            Assert.Equal("1.4.2", r.Version);
            Assert.True(r.Force);
            Assert.Equal(2, r.Notes.Count);
            Assert.True(RevitCadQC.Core.ToolVersion.IsNewer("1.4.2", "1.1.0"));
            Assert.True(RevitCadQC.Core.ToolVersion.IsNewer("2", "1.9.9"));
            Assert.False(RevitCadQC.Core.ToolVersion.IsNewer("1.1.0", "1.1.0"));
            Assert.False(RevitCadQC.Core.ToolVersion.IsNewer("garbage", "1.0"));
        }

        [Fact]
        public void Cad_linked_in_model_is_checked_at_its_link_position()
        {
            // no CAD folder at all: only a DWG/DXF "linked in Revit" with its placement (30 deg, moved 100 m / -50 m)
            var linked = Path.Combine(_dir, "linked", "consultant plan.dxf");
            Directory.CreateDirectory(Path.GetDirectoryName(linked));
            File.WriteAllText(linked, TestPlan.BuildDxf());
            var s = new QcSettings { CadFolder = "", OutputFolder = Path.Combine(_dir, "out") };
            s.ExtraCadFiles.Add(linked);
            s.FloorMappings.Add(new FloorMapping
            {
                CadFile = linked, LevelName = "Second Floor", FromModelLink = true,
                ManualRotationDeg = 30, ManualOffsetX = 100000, ManualOffsetY = -50000
            });
            var r = new QcEngine(s).Run(TestPlan.BuildSnapshot());
            foreach (var l in r.Log) _out.WriteLine(l);
            var f = Assert.Single(r.Floors);
            Assert.Equal("Second Floor", f.LevelName);
            Assert.StartsWith("Manual: Revit CAD link", f.Alignment.Method);
            Assert.True(f.Alignment.Reliable && f.Alignment.RmsMm < 10, f.Alignment.ToString()); // residual comes from the planted wrong walls
            Assert.Contains(r.Issues, i => i.IssueType == IssueType.ThicknessMismatch);
            Assert.True(File.Exists(Path.Combine(_dir, "out", "QC_Report.html")));
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
