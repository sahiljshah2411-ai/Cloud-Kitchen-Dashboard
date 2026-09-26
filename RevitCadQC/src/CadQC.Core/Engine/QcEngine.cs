using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using CadQC.Core.Alignment;
using CadQC.Core.Compare;
using CadQC.Core.Conversion;
using CadQC.Core.Dxf;
using CadQC.Core.Extraction;
using CadQC.Core.Geometry;
using CadQC.Core.Model;
using CadQC.Core.Report;
using CadQC.Core.Settings;

namespace CadQC.Core.Engine
{
    public sealed class QcProgress
    {
        public int Percent { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Runs the full CAD-vs-Revit technical QC:
    /// discover CAD → convert DWG→DXF → read → units → find plans → map floors → extract →
    /// align → compare → track against the previous run → write reports and CAD markups.
    /// </summary>
    public sealed class QcEngine
    {
        private readonly QcSettings _s;
        private readonly IProgress<QcProgress> _progress;
        private readonly CancellationToken _ct;

        public QcEngine(QcSettings settings, IProgress<QcProgress> progress = null, CancellationToken ct = default)
        {
            _s = settings;
            _progress = progress;
            _ct = ct;
        }

        public string OutputFolder => !string.IsNullOrWhiteSpace(_s.OutputFolder)
            ? _s.OutputFolder
            : Path.Combine(_s.CadFolder ?? ".", "_RevitCadQC");

        public QcReport Run(RevitSnapshot snap)
        {
            var sw = Stopwatch.StartNew();
            var report = new QcReport { ProjectName = snap.ProjectName, RevitFile = snap.DocumentPath, CadFolder = _s.CadFolder };
            Directory.CreateDirectory(OutputFolder);
            Report(2, "Scanning CAD folder…");

            // ---------- 1. discover & convert ----------
            var files = DiscoverCadFiles(report);
            if (files.Count == 0)
            {
                report.Log.Add("No DWG/DXF files found in " + _s.CadFolder);
                Finish(report, sw);
                return report;
            }
            var converter = new DwgConverter(Path.Combine(OutputFolder, ".dxf-cache"), _s.OdaConverterPath, _s.AcCoreConsolePath, _s.DxfOutputVersion);
            report.Log.Add("DWG converter: " + converter.ConverterDescription);

            var drawings = new List<(CadDrawing dwg, string units, string original)>();
            for (int i = 0; i < files.Count; i++)
            {
                _ct.ThrowIfCancellationRequested();
                var f = files[i];
                Report(5 + 25 * i / files.Count, $"Reading {Path.GetFileName(f)} ({i + 1}/{files.Count})…");
                try
                {
                    var dxf = converter.EnsureDxf(f);
                    var dwg = DxfReader.Load(dxf, _s.SkipFrozenAndOffLayers);
                    dwg.SourcePath = f;
                    // consultant profile first: it decides which layers are walls, also for unit detection
                    var used = UsedLayers(dwg).ToList();
                    double cov;
                    var prof = _s.SessionProfile != null && (cov = _s.SessionProfile.Coverage(used)) >= CadProfileStore.MatchThreshold
                        ? _s.SessionProfile
                        : CadProfileStore.BestMatch(used, out cov);
                    _profiles[f] = prof;
                    _s.Layers.UseProfile(prof);
                    report.Log.Add(prof != null
                        ? $"{Path.GetFileName(f)}: consultant layer profile '{prof.Name}' ({cov:P0} of its layers known)"
                        : $"{Path.GetFileName(f)}: no saved consultant profile - layers classified by {_s.Layers.Source}");
                    report.Log.Add("  layers: " + DescribeLayers(dwg));
                    // remember the original unit factor so markup can be written back in drawing units
                    string units = DrawingPreparer.NormaliseUnits(dwg, _s, out double factor);
                    drawings.Add((dwg, units, f));
                    _unitFactor[f] = factor;
                    report.Log.Add($"{Path.GetFileName(f)}: {dwg.Curves.Count} curves, {dwg.Texts.Count} texts, {dwg.Inserts.Count} block refs, {dwg.Dimensions.Count} dims. {units}");
                    foreach (var w in dwg.Warnings.Take(15)) report.Log.Add("  ! " + w);
                }
                catch (Exception ex)
                {
                    report.Log.Add($"FAILED {Path.GetFileName(f)}: {ex.Message}");
                    report.Issues.Add(new QcIssue
                    {
                        Id = "SYS-" + (report.Issues.Count + 1).ToString("000"),
                        Category = IssueCategory.CadDrafting,
                        IssueType = "CAD file could not be read",
                        Severity = Severity.Critical,
                        Title = "Could not read " + Path.GetFileName(f),
                        Description = ex.Message,
                        CadFile = f,
                        Key = "READ|" + f
                    });
                }
            }
            foreach (var l in converter.Log) report.Log.Add("  " + l);

            // ---------- 2. plans & floor mapping ----------
            Report(32, "Finding floor plans and mapping them to Revit levels…");
            var matcher = new LevelMatcher(snap.Levels);
            report.Log.AddRange(matcher.Log);
            var jobs = BuildJobs(drawings, snap, matcher, report);
            if (jobs.Count == 0) report.Log.Add("No CAD plan could be mapped to a Revit level. Add floor mappings in the settings.");

            // ---------- 3. per floor ----------
            var extractor = new CadExtractor(_s);
            var cache = new Dictionary<string, CadFloorData>();
            for (int j = 0; j < jobs.Count; j++)
            {
                _ct.ThrowIfCancellationRequested();
                var job = jobs[j];
                Report(35 + 55 * j / Math.Max(1, jobs.Count), $"Checking {job.Label} ({job.Level.Name})…");
                _s.Layers.UseProfile(_profiles.TryGetValue(job.Region.File, out var jp) ? jp : null);
                string ck = job.Region.File + "|" + job.Region.Title;
                if (!cache.TryGetValue(ck, out var cad))
                {
                    cad = extractor.Extract(job.Drawing, job.Region.Region, job.Region.Title);
                    cache[ck] = cad;
                }
                var fr = RunFloor(job, cad, snap);
                report.Floors.Add(fr.floor);
                report.Issues.AddRange(fr.issues);
            }

            // ---------- 4. track against previous run ----------
            Report(92, "Comparing with previous QC run…");
            var latest = Path.Combine(OutputFolder, "qc_report.json");
            IssueTracker.Merge(QcReport.LoadJson(latest), report);

            // ---------- 5. outputs ----------
            Report(95, "Writing reports and CAD markups…");
            Finish(report, sw);
            WriteOutputs(report, converter);
            Report(100, $"Done: {report.OpenCount} open issues ({report.Count(Severity.Critical)} critical).");
            return report;
        }

        private readonly Dictionary<string, CadLayerProfile> _profiles = new Dictionary<string, CadLayerProfile>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Layers that actually carry geometry or text (empty layers are not part of a consultant's convention).</summary>
        public static IEnumerable<string> UsedLayers(CadDrawing d) =>
            d.Curves.Select(c => c.Layer).Concat(d.Texts.Select(t => t.Layer)).Concat(d.Inserts.Select(i => i.Layer))
             .Concat(d.Dimensions.Select(x => x.Layer)).Where(l => !string.IsNullOrEmpty(l)).Distinct(StringComparer.OrdinalIgnoreCase);

        private string DescribeLayers(CadDrawing d)
        {
            var parts = new List<string>();
            foreach (var g in UsedLayers(d).GroupBy(l => _s.Layers.Classify(l)).OrderBy(g => g.Key))
            {
                if (g.Key == LayerCategory.Unknown || g.Key == LayerCategory.Ignore)
                    parts.Add($"{g.Key}: {g.Count()} layer(s)");
                else
                    parts.Add($"{g.Key}: {string.Join(", ", g.Take(8))}{(g.Count() > 8 ? $" (+{g.Count() - 8})" : "")}");
            }
            return string.Join(" | ", parts);
        }

        private readonly Dictionary<string, double> _unitFactor = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        private void Finish(QcReport report, Stopwatch sw)
        {
            report.DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
            report.Issues = report.Issues
                .OrderBy(i => i.Status == IssueStatus.Resolved ? 1 : 0)
                .ThenBy(i => i.Floor)
                .ThenBy(i => i.Severity)
                .ThenBy(i => i.Category)
                .ThenBy(i => i.Id)
                .ToList();
        }

        private void Report(int pct, string msg) => _progress?.Report(new QcProgress { Percent = pct, Message = msg });

        private List<string> DiscoverCadFiles(QcReport report)
        {
            if (string.IsNullOrWhiteSpace(_s.CadFolder) || !Directory.Exists(_s.CadFolder))
            {
                report.Log.Add("CAD folder not found: " + _s.CadFolder);
                return new List<string>();
            }
            var opt = _s.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var all = Directory.GetFiles(_s.CadFolder, "*.*", opt)
                .Where(f => f.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "_RevitCadQC") && !Path.GetFileName(f).Contains("_QC_markup")
                            && !Path.GetFileName(f).StartsWith("~", StringComparison.Ordinal))
                .ToList();
            // a DXF with the same name as a DWG is usually an old export: prefer the DWG
            var dwgNames = new HashSet<string>(all.Where(f => f.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)).Select(f => Path.ChangeExtension(f, null)), StringComparer.OrdinalIgnoreCase);
            return all.Where(f => f.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) || !dwgNames.Contains(Path.ChangeExtension(f, null)))
                      .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private sealed class Job
        {
            public CadDrawing Drawing;
            public CadPlanRegion Region;
            public string UnitsNote;
            public QcLevel Level;
            public string Label;
            public string Code;
            public FloorMapping Manual;
        }

        private List<Job> BuildJobs(List<(CadDrawing dwg, string units, string original)> drawings, RevitSnapshot snap, LevelMatcher matcher, QcReport report)
        {
            var jobs = new List<Job>();
            var regionsByFile = drawings.ToDictionary(d => d.original, d => (d, regions: DrawingPreparer.FindPlans(d.dwg, _s)));
            foreach (var kv in regionsByFile)
                foreach (var r in kv.Value.regions) report.Log.Add("Plan found: " + r);

            // explicit mappings first
            var mappedLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in _s.FloorMappings.Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.CadFile) && !string.IsNullOrWhiteSpace(m.LevelName)))
            {
                var file = regionsByFile.Keys.FirstOrDefault(k => Path.GetFileName(k).IndexOf(m.CadFile, StringComparison.OrdinalIgnoreCase) >= 0);
                var level = snap.Levels.FirstOrDefault(l => string.Equals(l.Name, m.LevelName, StringComparison.OrdinalIgnoreCase));
                if (file == null || level == null)
                {
                    report.Log.Add($"Floor mapping '{m.CadFile}' → '{m.LevelName}' ignored: {(file == null ? "CAD file" : "Revit level")} not found.");
                    continue;
                }
                var entry = regionsByFile[file];
                var region = !string.IsNullOrWhiteSpace(m.RegionTitle)
                    ? entry.regions.FirstOrDefault(r => (r.Title ?? "").IndexOf(m.RegionTitle, StringComparison.OrdinalIgnoreCase) >= 0)
                    : (entry.regions.Count == 1 ? entry.regions[0] : new CadPlanRegion { File = file });
                if (region == null) { report.Log.Add($"Plan title '{m.RegionTitle}' not found in {Path.GetFileName(file)}."); continue; }
                jobs.Add(new Job { Drawing = entry.d.dwg, Region = region, UnitsNote = entry.d.units, Level = level, Label = level.Name, Code = Code(level.Name), Manual = m });
                mappedLevels.Add(level.Name);
            }

            if (_s.AutoMapFloors)
            {
                foreach (var kv in regionsByFile)
                {
                    foreach (var r in kv.Value.regions)
                    {
                        if (r.Floors.Count == 0)
                        {
                            report.Log.Add($"Could not tell which floor {r} is (name the file e.g. '2ND FLOOR PLAN.dwg' or add a floor mapping).");
                            continue;
                        }
                        foreach (var f in r.Floors)
                        {
                            var level = matcher.Find(f);
                            if (level == null) { report.Log.Add($"No Revit level for {LevelMatcher.FloorLabel(f)} ({r})."); continue; }
                            if (mappedLevels.Contains(level.Name)) continue;
                            jobs.Add(new Job
                            {
                                Drawing = kv.Value.d.dwg,
                                Region = r,
                                UnitsNote = kv.Value.d.units,
                                Level = level,
                                Label = LevelMatcher.FloorLabel(f),
                                Code = LevelMatcher.FloorCode(f),
                                Manual = _s.FloorMappings.FirstOrDefault(m => string.Equals(m.LevelName, level.Name, StringComparison.OrdinalIgnoreCase))
                            });
                        }
                    }
                }
            }

            // single plan + single storey: nothing to decide
            if (jobs.Count == 0 && regionsByFile.Count == 1 && regionsByFile.First().Value.regions.Count == 1)
            {
                var stories = snap.Levels.Where(l => l.IsBuildingStory).ToList();
                if (stories.Count == 1)
                {
                    var e = regionsByFile.First().Value;
                    jobs.Add(new Job { Drawing = e.d.dwg, Region = e.regions[0], UnitsNote = e.d.units, Level = stories[0], Label = stories[0].Name, Code = Code(stories[0].Name) });
                }
            }

            // two plans on the same level: make labels unique
            foreach (var g in jobs.GroupBy(j => j.Level.Name).Where(g => g.Count() > 1))
                foreach (var j in g) j.Label += " — " + Path.GetFileNameWithoutExtension(j.Region.File);
            return jobs;
        }

        private static string Code(string levelName)
        {
            var f = TextParsing.ParseFloors(levelName);
            if (f.Count == 1) return LevelMatcher.FloorCode(f[0]);
            var c = new string((levelName ?? "LV").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            return c.Length > 6 ? c.Substring(0, 6) : (c.Length == 0 ? "LV" : c);
        }

        private (FloorResult floor, List<QcIssue> issues) RunFloor(Job job, CadFloorData cadRaw, RevitSnapshot snap)
        {
            string lvl = job.Level.Name;
            var rWalls = snap.Walls.Where(w => w.Levels.Contains(lvl) || (w.Levels.Count == 0 && w.BaseLevel == lvl)).ToList();
            var rOpen = snap.Openings.Where(o => o.LevelName == lvl).ToList();
            var rCols = snap.Columns.Where(c => c.Levels.Contains(lvl) || c.LevelName == lvl).ToList();
            var rRooms = snap.Rooms.Where(r => r.LevelName == lvl).ToList();

            var fr = new FloorResult
            {
                Floor = job.Label,
                FloorCode = job.Code,
                LevelName = lvl,
                CadFile = job.Region.File,
                RegionTitle = job.Region.Title,
                UnitsNote = job.UnitsNote,
                CadUnitToMm = _unitFactor.TryGetValue(job.Region.File, out var uf) ? uf : 1
            };
            fr.Notes.AddRange(cadRaw.Notes);
            fr.CadCounts["Walls"] = cadRaw.Walls.Count;
            fr.CadCounts["Doors"] = cadRaw.Openings.Count(o => o.Kind == OpeningKind.Door);
            fr.CadCounts["Windows"] = cadRaw.Openings.Count(o => o.Kind != OpeningKind.Door);
            fr.CadCounts["Columns"] = cadRaw.Columns.Count;
            fr.CadCounts["Rooms"] = cadRaw.Rooms.Count;
            fr.CadCounts["Grids"] = cadRaw.Grids.Count(g => g.Name != null);
            fr.CadCounts["Dimensions"] = cadRaw.Dimensions.Count;
            fr.RevitCounts["Walls"] = rWalls.Count;
            fr.RevitCounts["Doors"] = rOpen.Count(o => o.Kind == OpeningKind.Door);
            fr.RevitCounts["Windows"] = rOpen.Count(o => o.Kind != OpeningKind.Door);
            fr.RevitCounts["Columns"] = rCols.Count;
            fr.RevitCounts["Rooms"] = rRooms.Count;
            fr.RevitCounts["Grids"] = snap.Grids.Count;

            // ---- align ----
            var align = new AutoAligner(_s).Align(cadRaw, rWalls, snap.Grids, job.Manual);
            fr.Alignment = align;
            var cad = Transform(cadRaw, align.Transform);

            var ctx = new FloorContext
            {
                FloorLabel = job.Label,
                FloorCode = job.Code,
                LevelName = lvl,
                CadFile = job.Region.File,
                Settings = _s,
                CadToRevit = align.Transform,
                Cad = cad,
                RevitWalls = rWalls,
                RevitOpenings = rOpen,
                RevitColumns = rCols,
                RevitRooms = rRooms,
                RevitGrids = snap.Grids
            };

            if (!align.Reliable)
            {
                ctx.Add(IssueCategory.Alignment, IssueType.AlignmentWarning, Severity.Critical,
                    $"CAD and Revit could not be aligned reliably ({align.InlierRatio:P0} match)", null,
                    $"Best alignment '{align.Method}' matched only {align.InlierRatio:P0} of CAD walls to Revit walls (RMS {align.RmsMm:0.0} mm). " +
                    "Results for this floor may contain false positives. Use matching grid names, or enter a manual offset/rotation in the floor mapping.");
            }
            if (cad.Walls.Count == 0)
            {
                ctx.Add(IssueCategory.CadDrafting, "No walls found in CAD", Severity.Critical, "No double-line walls found in the CAD plan", null,
                    "Check the wall layer names in the settings (WallLayers). Layers used: " + string.Join(", ", cad.WallLayersUsed));
            }

            if (_s.CheckWalls) new WallComparer().Compare(ctx);
            if (_s.CheckDoors || _s.CheckWindows) new OpeningComparer().Compare(ctx);
            if (_s.CheckColumns) new ColumnComparer().Compare(ctx);
            if (_s.CheckGrids) new GridComparer().Compare(ctx);
            if (_s.CheckRooms) new RoomComparer().Compare(ctx);
            if (_s.CheckDimensions) new DimensionComparer().Compare(ctx);

            fr.IssueCount = ctx.Issues.Count;
            fr.Overlay = BuildOverlay(job, cad, cadRaw, align.Transform, rWalls, rOpen, rCols, snap.Grids);
            return (fr, ctx.Issues);
        }

        private FloorOverlay BuildOverlay(Job job, CadFloorData cad, CadFloorData cadRaw, Similarity2 t, List<QcWall> walls, List<QcOpening> openings, List<QcColumn> cols, List<QcGrid> grids)
        {
            var ov = new FloorOverlay();
            Func<Vec2, bool> inRegion = p => job.Region.Region == null || job.Region.Region.Value.Contains(p);
            foreach (var c in job.Drawing.Curves)
            {
                if (_s.IsExcluded(c.Layer)) continue;
                bool relevant = _s.IsLayer(LayerCategory.Wall, c.Layer) || _s.IsLayer(LayerCategory.Door, c.Layer) || _s.IsLayer(LayerCategory.Window, c.Layer)
                                || _s.IsLayer(LayerCategory.Column, c.Layer) || cad.WallLayersUsed.Contains(c.Layer);
                if (!relevant) continue;
                foreach (var sgm in c.Segments())
                {
                    if (!inRegion(sgm.Mid)) continue;
                    ov.CadLines.Add(t.Apply(sgm));
                    if (ov.CadLines.Count > 80000) break;
                }
            }
            foreach (var w in walls)
            {
                var n = w.Centerline.Dir.Perp * (w.Thickness / 2);
                ov.RevitWalls.Add(new List<Vec2> { w.Centerline.A + n, w.Centerline.B + n, w.Centerline.B - n, w.Centerline.A - n });
            }
            foreach (var c in cols)
                ov.RevitColumns.Add(c.Outline.Count > 0 ? c.Outline : new OrientedRect { Center = c.Center, Width = c.Width, Depth = c.Depth, Angle = c.Angle }.Corners());
            foreach (var o in openings) ov.RevitOpenings.Add((o.Position, o.Width, o.Kind.ToString()));
            ov.RevitGrids.AddRange(grids.Select(g => g.Line));
            var b = Box2.Empty;
            foreach (var s in ov.CadLines) b = b.Include(s.A).Include(s.B);
            foreach (var w in ov.RevitWalls) foreach (var p in w) b = b.Include(p);
            ov.Bounds = b;
            return ov;
        }

        /// <summary>Copies CAD floor data into the Revit frame.</summary>
        public static CadFloorData Transform(CadFloorData c, Similarity2 t)
        {
            double rot = t.Rotation;
            return new CadFloorData
            {
                SourceFile = c.SourceFile,
                RegionTitle = c.RegionTitle,
                Notes = c.Notes,
                WallLayersUsed = c.WallLayersUsed,
                Bounds = Box2.FromPoints(new[] { new Vec2(c.Bounds.MinX, c.Bounds.MinY), new Vec2(c.Bounds.MaxX, c.Bounds.MinY), new Vec2(c.Bounds.MaxX, c.Bounds.MaxY), new Vec2(c.Bounds.MinX, c.Bounds.MaxY) }
                    .Where(p => !c.Bounds.IsEmpty).Select(t.Apply)),
                Walls = c.Walls.Select(w => new QcWall
                {
                    Id = w.Id, Centerline = t.Apply(w.Centerline), Thickness = w.Thickness * t.Scale, AnnotatedThickness = w.AnnotatedThickness,
                    AnnotationText = w.AnnotationText, Layer = w.Layer, Handles = w.Handles
                }).ToList(),
                Openings = c.Openings.Select(o => new QcOpening
                {
                    Id = o.Id, Kind = o.Kind, Position = t.Apply(o.Position), Width = o.Width * t.Scale, Angle = GeoMath.NormalizeUndirected(o.Angle + rot),
                    BlockName = o.BlockName, Layer = o.Layer, Handle = o.Handle, Tag = o.Tag, WidthSource = o.WidthSource, HostWallId = o.HostWallId
                }).ToList(),
                Columns = c.Columns.Select(k => new QcColumn
                {
                    Id = k.Id, Center = t.Apply(k.Center), Width = k.Width * t.Scale, Depth = k.Depth * t.Scale, Angle = GeoMath.NormalizeUndirected(k.Angle + rot),
                    IsCircular = k.IsCircular, Layer = k.Layer, Handle = k.Handle, Outline = k.Outline.Select(t.Apply).ToList()
                }).ToList(),
                Rooms = c.Rooms.Select(r => new QcRoom
                {
                    Id = r.Id, Name = r.Name, Position = t.Apply(r.Position), SizeA = r.SizeA, SizeB = r.SizeB, SizeText = r.SizeText, AreaM2 = r.AreaM2,
                    Layer = r.Layer, Handle = r.Handle
                }).ToList(),
                Grids = c.Grids.Select(g => new QcGrid { Id = g.Id, Name = g.Name, Line = t.Apply(g.Line), Layer = g.Layer, Handle = g.Handle }).ToList(),
                Dimensions = c.Dimensions.Select(d => new QcDimension
                {
                    P1 = t.Apply(d.P1), P2 = t.Apply(d.P2), Direction = t.ApplyVector(d.Direction).Normalized(), ValueMm = d.ValueMm, GeometricMm = d.GeometricMm * t.Scale,
                    Text = d.Text, IsOverridden = d.IsOverridden, TextPosition = t.Apply(d.TextPosition), Layer = d.Layer, Handle = d.Handle
                }).ToList(),
                WallLayerSegments = c.WallLayerSegments.Select(t.Apply).ToList(),
                UnpairedWallLines = c.UnpairedWallLines.Select(t.Apply).ToList()
            };
        }

        private void WriteOutputs(QcReport report, DwgConverter converter)
        {
            string stamp = report.RunUtc.ToLocalTime().ToString("yyyyMMdd_HHmm");
            string dir = OutputFolder;
            var hist = Path.Combine(dir, "history");
            Directory.CreateDirectory(hist);

            void Try(string what, Action a)
            {
                try { a(); }
                catch (Exception ex) { report.Log.Add($"Could not write {what}: {ex.Message}"); }
            }

            if (_s.WriteHtmlReport) Try("HTML report", () => { var p = Path.Combine(dir, "QC_Report.html"); HtmlReportWriter.Write(report, p); report.OutputFiles.Add(p); });
            if (_s.WriteCsv) Try("CSV", () => { var p = Path.Combine(dir, "QC_Issues.csv"); CsvReportWriter.Write(report, p); report.OutputFiles.Add(p); });
            if (_s.WriteExcelXml) Try("Excel", () => { var p = Path.Combine(dir, "QC_Issues.xlsx"); XlsxReportWriter.Write(report, p); report.OutputFiles.Add(p); });

            if (_s.WriteCadMarkupDxf || _s.WriteAutoCadScript)
            {
                var markupDir = Path.Combine(dir, "CAD_Markup");
                Directory.CreateDirectory(markupDir);
                foreach (var g in report.Floors.GroupBy(f => f.CadFile))
                {
                    string baseName = Path.GetFileNameWithoutExtension(g.Key) + "_QC_markup";
                    var floors = g.ToList();
                    var issues = report.Issues.Where(i => string.Equals(i.CadFile, g.Key, StringComparison.OrdinalIgnoreCase) && i.Status != IssueStatus.Resolved).ToList();
                    double unit = floors[0].CadUnitToMm;
                    if (_s.WriteCadMarkupDxf)
                        Try("CAD markup DXF", () =>
                        {
                            var p = Path.Combine(markupDir, baseName + ".dxf");
                            CadMarkupWriter.WriteDxf(p, issues, floors, unit, _s.IncludeRevitOverlayInCadMarkup);
                            report.OutputFiles.Add(p);
                            if (_s.ConvertMarkupToDwg)
                            {
                                var dwg = converter.DxfToDwg(p, markupDir);
                                if (dwg != null) report.OutputFiles.Add(dwg);
                            }
                        });
                    if (_s.WriteAutoCadScript)
                        Try("AutoCAD script", () =>
                        {
                            var p = Path.Combine(markupDir, baseName + ".scr");
                            CadMarkupWriter.WriteAutoCadScript(p, issues, unit);
                            report.OutputFiles.Add(p);
                        });
                }
            }

            if (_s.WriteJson)
                Try("JSON", () =>
                {
                    var latest = Path.Combine(dir, "qc_report.json");
                    report.OutputFiles.Add(latest);
                    report.SaveJson(latest);
                    File.Copy(latest, Path.Combine(hist, $"qc_report_{stamp}.json"), true);
                });
        }
    }
}
