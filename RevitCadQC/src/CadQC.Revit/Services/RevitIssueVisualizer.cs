using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using CadQC.Core.Geometry;
using CadQC.Core.Report;
using CadQC.Core.Settings;

namespace CadQC.Revit.Services
{
    /// <summary>
    /// Shows the QC result inside Revit:
    ///  • one "QC - &lt;level&gt;" floor plan per checked floor, with the CAD plan linked in at the aligned position,
    ///  • issue clouds, missing-geometry lines and ID labels drawn as detail items on "QC Critical/Major/Minor/Info" line styles,
    ///  • elements with issues overridden in red/orange/yellow in the QC plans and a "QC 3D" view.
    /// Nothing outside the QC views is changed (optionally the issue IDs are written to Comments).
    /// </summary>
    public sealed class RevitIssueVisualizer
    {
        public const string ViewPrefix = "QC - ";
        public const string View3dPrefix = "QC 3D - ";
        private const double Ft = 304.8;
        private readonly Document _doc;
        private readonly QcSettings _s;
        public Dictionary<string, ElementId> ViewsByLevel { get; } = new Dictionary<string, ElementId>();
        public ElementId View3dId { get; private set; } = ElementId.InvalidElementId;
        public List<string> Log { get; } = new List<string>();

        public RevitIssueVisualizer(Document doc, QcSettings settings)
        {
            _doc = doc;
            _s = settings;
        }

        public void Apply(QcReport report)
        {
            using (var tx = new Transaction(_doc, "CAD QC – highlight issues"))
            {
                tx.Start();
                var failOpt = tx.GetFailureHandlingOptions();
                failOpt.SetFailuresPreprocessor(new SwallowWarnings());
                tx.SetFailureHandlingOptions(failOpt);

                var styles = EnsureLineStyles();
                var solid = new FilteredElementCollector(_doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                    .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
                var textType = _doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                string stamp = report.RunUtc.ToLocalTime().ToString("yyyy-MM-dd HHmm");
                var open = report.Issues.Where(i => i.Status != IssueStatus.Resolved && i.Status != IssueStatus.Accepted).ToList();

                if (_s.CreateQcViews)
                {
                    foreach (var floor in report.Floors)
                    {
                        var level = new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault(l => l.Name == floor.LevelName);
                        if (level == null) continue;
                        var view = CreatePlan(level, $"{ViewPrefix}{floor.Floor} - {stamp}");
                        if (view == null) continue;
                        ViewsByLevel[floor.LevelName] = view.Id;
                        var issues = open.Where(i => i.LevelName == floor.LevelName && i.Floor == floor.Floor).ToList();

                        if (_s.LinkCadIntoQcViews) LinkCad(view, floor);
                        foreach (var i in issues) DrawIssue(view, i, styles, textType);
                        OverrideElements(view, issues, solid);
                        CropTo(view, floor, issues);
                    }
                }

                if (_s.Create3dQcView)
                {
                    var vft = new FilteredElementCollector(_doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                        .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);
                    if (vft != null)
                    {
                        var v3 = View3D.CreateIsometric(_doc, vft.Id);
                        v3.Name = Unique($"{View3dPrefix}{stamp}");
                        v3.DetailLevel = ViewDetailLevel.Medium;
                        OverrideElements(v3, open, solid);
                        View3dId = v3.Id;
                    }
                }

                if (_s.WriteIssueIdsToComments)
                {
                    foreach (var g in open.SelectMany(i => i.RevitElementIds.Select(id => (id, i.Id))).GroupBy(x => x.id))
                    {
                        var e = Get(g.Key);
                        var p = e?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                        if (p != null && !p.IsReadOnly) p.Set("CAD QC: " + string.Join(", ", g.Select(x => x.Item2)));
                    }
                }
                tx.Commit();
            }
        }

        #region views

        private ViewPlan CreatePlan(Level level, string name)
        {
            var vft = new FilteredElementCollector(_doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.FloorPlan);
            if (vft == null) { Log.Add("No floor plan view type in the project."); return null; }
            var view = ViewPlan.Create(_doc, vft.Id, level.Id);
            view.Name = Unique(name);
            view.DetailLevel = ViewDetailLevel.Fine;
            view.Scale = 100;
            try { if (view.ViewTemplateId != ElementId.InvalidElementId) view.ViewTemplateId = ElementId.InvalidElementId; } catch { }
            return view;
        }

        private string Unique(string name)
        {
            var names = new HashSet<string>(new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name));
            string n = name; int k = 2;
            while (names.Contains(n)) n = $"{name} ({k++})";
            return n;
        }

        private void CropTo(View view, FloorResult floor, List<QcIssue> issues)
        {
            var b = floor.Overlay?.Bounds ?? Box2.Empty;
            foreach (var i in issues.Where(i => i.RevitLocation.HasValue)) b = b.Include(i.RevitLocation.Value);
            if (b.IsEmpty) return;
            b = b.Inflate(3000);
            try
            {
                var box = view.CropBox;
                var inv = box.Transform.Inverse;
                var p0 = inv.OfPoint(new XYZ(b.MinX / Ft, b.MinY / Ft, 0));
                var p1 = inv.OfPoint(new XYZ(b.MaxX / Ft, b.MaxY / Ft, 0));
                box.Min = new XYZ(Math.Min(p0.X, p1.X), Math.Min(p0.Y, p1.Y), box.Min.Z);
                box.Max = new XYZ(Math.Max(p0.X, p1.X), Math.Max(p0.Y, p1.Y), box.Max.Z);
                view.CropBox = box;
                view.CropBoxActive = true;
                view.CropBoxVisible = false;
            }
            catch (Exception ex) { Log.Add("Crop failed: " + ex.Message); }
        }

        /// <summary>Links the CAD file into the QC plan (this view only) and moves it onto the model with the QC alignment.</summary>
        private void LinkCad(View view, FloorResult floor)
        {
            if (string.IsNullOrEmpty(floor.CadFile) || !File.Exists(floor.CadFile) || floor.Alignment == null) return;
            try
            {
                var opt = new DWGImportOptions
                {
                    ThisViewOnly = true,
                    Placement = ImportPlacement.Origin,
                    ColorMode = ImportColorMode.BlackAndWhite,
                    Unit = UnitFor(floor.CadUnitToMm),
                    VisibleLayersOnly = true
                };
                if (opt.Unit == ImportUnit.Custom) opt.CustomScale = floor.CadUnitToMm / Ft;
                if (!_doc.Link(floor.CadFile, opt, view, out ElementId id) || id == ElementId.InvalidElementId) return;
                var t = floor.Alignment.Transform;
                if (Math.Abs(t.Rotation) > 1e-9)
                    ElementTransformUtils.RotateElement(_doc, id, Line.CreateBound(XYZ.Zero, XYZ.BasisZ), t.Rotation);
                ElementTransformUtils.MoveElement(_doc, id, new XYZ(t.Tx / Ft, t.Ty / Ft, 0));
                var ov = new OverrideGraphicSettings();
                ov.SetHalftone(true);
                view.SetElementOverrides(id, ov);
            }
            catch (Exception ex) { Log.Add($"Could not link {Path.GetFileName(floor.CadFile)} into the QC view: {ex.Message}"); }
        }

        private static ImportUnit UnitFor(double mmPerUnit)
        {
            if (Math.Abs(mmPerUnit - 1) < 1e-6) return ImportUnit.Millimeter;
            if (Math.Abs(mmPerUnit - 10) < 1e-6) return ImportUnit.Centimeter;
            if (Math.Abs(mmPerUnit - 1000) < 1e-6) return ImportUnit.Meter;
            if (Math.Abs(mmPerUnit - 25.4) < 1e-6) return ImportUnit.Inch;
            if (Math.Abs(mmPerUnit - 304.8) < 1e-6) return ImportUnit.Foot;
            return ImportUnit.Custom;
        }

        #endregion

        #region drawing

        private Dictionary<Severity, GraphicsStyle> EnsureLineStyles()
        {
            var lines = _doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            var defs = new Dictionary<Severity, (string name, Color color, int weight)>
            {
                { Severity.Critical, ("QC Critical", new Color(230, 0, 0), 10) },
                { Severity.Major, ("QC Major", new Color(255, 128, 0), 8) },
                { Severity.Minor, ("QC Minor", new Color(220, 180, 0), 6) },
                { Severity.Info, ("QC Info", new Color(0, 140, 200), 4) }
            };
            var res = new Dictionary<Severity, GraphicsStyle>();
            foreach (var kv in defs)
            {
                Category sub = null;
                foreach (Category c in lines.SubCategories) if (c.Name == kv.Value.name) { sub = c; break; }
                if (sub == null) sub = _doc.Settings.Categories.NewSubcategory(lines, kv.Value.name);
                sub.LineColor = kv.Value.color;
                sub.SetLineWeight(kv.Value.weight, GraphicsStyleType.Projection);
                res[kv.Key] = sub.GetGraphicsStyle(GraphicsStyleType.Projection);
            }
            return res;
        }

        private void DrawIssue(View view, QcIssue i, Dictionary<Severity, GraphicsStyle> styles, ElementId textType)
        {
            if (!i.RevitLocation.HasValue) return;
            var style = styles[i.Severity];
            double z = (view as ViewPlan)?.GenLevel?.ProjectElevation ?? 0;
            var c = new XYZ(i.RevitLocation.Value.X / Ft, i.RevitLocation.Value.Y / Ft, z);
            double r = Math.Max(300, i.MarkerRadius) / Ft;
            try
            {
                // revision-cloud style scallops
                int n = 14;
                for (int k = 0; k < n; k++)
                {
                    double a0 = 2 * Math.PI * k / n, a1 = 2 * Math.PI * (k + 1) / n, am = (a0 + a1) / 2;
                    var p0 = c + new XYZ(Math.Cos(a0), Math.Sin(a0), 0) * r;
                    var p1 = c + new XYZ(Math.Cos(a1), Math.Sin(a1), 0) * r;
                    var pm = c + new XYZ(Math.Cos(am), Math.Sin(am), 0) * (r * 1.12);
                    var dc = _doc.Create.NewDetailCurve(view, Arc.Create(p0, p1, pm));
                    dc.LineStyle = style;
                }
                if (i.RevitPath != null)
                {
                    for (int k = 0; k + 1 < i.RevitPath.Count; k++)
                    {
                        var a = new XYZ(i.RevitPath[k].X / Ft, i.RevitPath[k].Y / Ft, z);
                        var b = new XYZ(i.RevitPath[k + 1].X / Ft, i.RevitPath[k + 1].Y / Ft, z);
                        if (a.DistanceTo(b) < _doc.Application.ShortCurveTolerance * 2) continue;
                        var dl = _doc.Create.NewDetailCurve(view, Line.CreateBound(a, b));
                        dl.LineStyle = style;
                    }
                }
                if (textType != ElementId.InvalidElementId)
                {
                    var tp = c + new XYZ(r * 0.8, r * 0.8, 0);
                    TextNote.Create(_doc, view.Id, tp, $"{i.Id}  {i.Title}", textType);
                }
            }
            catch (Exception ex) { Log.Add($"Could not draw {i.Id}: {ex.Message}"); }
        }

        private void OverrideElements(View view, IEnumerable<QcIssue> issues, FillPatternElement solid)
        {
            var worst = new Dictionary<string, Severity>();
            foreach (var i in issues)
                foreach (var id in i.RevitElementIds)
                    if (!worst.TryGetValue(id, out var s) || i.Severity < s) worst[id] = i.Severity;
            foreach (var kv in worst)
            {
                var e = Get(kv.Key);
                if (e == null) continue;
                var col = kv.Value == Severity.Critical ? new Color(230, 0, 0) : kv.Value == Severity.Major ? new Color(255, 128, 0)
                        : kv.Value == Severity.Minor ? new Color(230, 190, 0) : new Color(0, 140, 200);
                var ov = new OverrideGraphicSettings();
                ov.SetProjectionLineColor(col);
                ov.SetCutLineColor(col);
                ov.SetProjectionLineWeight(8);
                ov.SetCutLineWeight(8);
                if (solid != null)
                {
                    ov.SetSurfaceForegroundPatternId(solid.Id);
                    ov.SetSurfaceForegroundPatternColor(col);
                    ov.SetCutForegroundPatternId(solid.Id);
                    ov.SetCutForegroundPatternColor(col);
                }
                try { view.SetElementOverrides(e.Id, ov); } catch { /* element not visible in this view type */ }
            }
        }

        private Element Get(string id)
        {
            if (string.IsNullOrEmpty(id) || id.StartsWith("L", StringComparison.Ordinal)) return null; // linked element
            if (!long.TryParse(id, out long v)) return null;
#if REVIT2024 || REVIT2025 || REVIT2026 || REVIT2027
            return _doc.GetElement(new ElementId(v));
#else
            return _doc.GetElement(new ElementId((int)v));
#endif
        }

        #endregion

        /// <summary>Removes every QC view (and with it the view-specific markup and linked CAD) and the QC comments.</summary>
        public static int Clear(Document doc)
        {
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && (v.Name.StartsWith(ViewPrefix, StringComparison.Ordinal) || v.Name.StartsWith(View3dPrefix, StringComparison.Ordinal)))
                .Select(v => v.Id).ToList();
            using (var tx = new Transaction(doc, "CAD QC – clear"))
            {
                tx.Start();
                if (views.Count > 0) doc.Delete(views);
                foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                {
                    var p = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    var s = p?.AsString();
                    if (s != null && s.StartsWith("CAD QC:", StringComparison.Ordinal) && !p.IsReadOnly) p.Set("");
                }
                tx.Commit();
            }
            return views.Count;
        }

        private sealed class SwallowWarnings : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                foreach (var f in a.GetFailureMessages())
                    if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
                return FailureProcessingResult.Continue;
            }
        }
    }
}
