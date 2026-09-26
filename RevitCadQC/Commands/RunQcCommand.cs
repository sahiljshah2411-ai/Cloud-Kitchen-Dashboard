using System.Collections.Generic;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitCadQC.Core;
using RevitCadQC.Core.Engine;
using RevitCadQC.Core.Report;
using RevitCadQC.Core.Settings;
using RevitCadQC.Core.Revit;
using RevitCadQC.UI;
using Newtonsoft.Json;

namespace RevitCadQC.Commands
{
    /// <summary>Run QC: read the model, compare with every CAD plan in the folder, highlight in Revit and write the reports.</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class RunQcCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uidoc = data.Application.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null || doc.IsFamilyDocument) { message = "Open a Revit project first."; return Result.Failed; }

            var settings = SettingsStore.Load(doc);
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).Select(l => l.Name).ToArray();
            List<ModelCadLink> links;
            try { links = CadLinkReader.Read(doc).Where(l => l.FileExists).ToList(); }
            catch { links = new List<ModelCadLink>(); }
            var dlg = new SetupWindow(settings, levels, SettingsStore.PathFor(doc), links.Count);
            new System.Windows.Interop.WindowInteropHelper(dlg) { Owner = data.Application.MainWindowHandle };
            if (dlg.ShowDialog() != true) return Result.Cancelled;
            SettingsStore.Save(doc, settings);

            // CAD already linked in the model: check it too, with the link's own placement as exact alignment candidate
            if (settings.UseModelCadLinks)
            {
                foreach (var l in links)
                {
                    settings.ExtraCadFiles.Add(l.Path);
                    settings.FloorMappings.Add(new FloorMapping
                    {
                        CadFile = l.Path,
                        LevelName = l.LevelName,
                        ManualRotationDeg = l.RotationDeg,
                        ManualOffsetX = l.OffsetXmm,
                        ManualOffsetY = l.OffsetYmm,
                        FromModelLink = true
                    });
                }
            }

            try
            {
                // 0. which CAD layers are what: saved consultant profile, or review once
                var scan = ProgressWindow.Run("Reading CAD layers", (p, ct) => LayerScanner.Scan(settings, p, ct));
                if (scan == null) return Result.Cancelled;
                var readable = scan.Where(f => f.Error == null).ToList();
                var review = dlg.ForceLayerReview ? readable : readable.Where(f => f.Profile == null).ToList();
                if (review.Count > 0)
                {
                    var name = review.Select(f => f.Profile?.Name).FirstOrDefault(n => n != null)
                               ?? new DirectoryInfo(settings.CadFolder).Name;
                    var lr = new LayerReviewWindow(LayerScanner.Merge(review, settings.Layers), name, review.Select(f => Path.GetFileName(f.Path)));
                    new System.Windows.Interop.WindowInteropHelper(lr) { Owner = data.Application.MainWindowHandle };
                    if (lr.ShowDialog() != true) return Result.Cancelled;
                    settings.SessionProfile = lr.Result;
                }

                // 1. Revit model → neutral snapshot (Revit API thread)
                var extractor = new RevitExtractor(doc, settings.CutPlaneHeight);
                var snap = extractor.Extract(settings.IncludeLinkedModels);

                // 2. CAD conversion, extraction, alignment and comparison (worker thread)
                var engine = new QcEngine(settings);
                var report = ProgressWindow.Run("Revit vs CAD QC", (progress, ct) => new QcEngine(settings, progress, ct).Run(snap));
                if (report == null) return Result.Cancelled;
                report.Log.AddRange(extractor.Warnings.Take(50).Select(w => "Revit: " + w));
                report.Log.InsertRange(0, SettingsStore.LastConfigLog.Select(l => "Config: " + l));
                report.Log.Insert(0, ToolVersion.Display + " | central rules: " + (CadQcConfig.FindFile() ?? "none"));
                App.LastReportPath = Path.Combine(engine.OutputFolder, "qc_report.json");

                // 3. highlight in Revit
                var vis = new RevitIssueVisualizer(doc, settings);
                vis.Apply(report);
                report.Log.AddRange(vis.Log);

                var html = report.OutputFiles.FirstOrDefault(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
                var td = new TaskDialog("CAD QC finished")
                {
                    MainInstruction = $"{report.OpenCount} open issues on {report.Floors.Count} floor(s)",
                    MainContent = $"Critical: {report.Count(Severity.Critical)}   Major: {report.Count(Severity.Major)}   Minor: {report.Count(Severity.Minor)}   Info: {report.Count(Severity.Info)}\n" +
                                  $"Resolved since last run: {report.Issues.Count(i => i.Status == IssueStatus.Resolved)}\n\n" +
                                  $"QC plans created: {vis.ViewsByLevel.Count}. Reports and CAD markups: {engine.OutputFolder}" +
                                  (report.Floors.Count == 0 ? "\n\nNo CAD plan could be matched to a Revit level. Open the run log in the HTML report, then add a floor mapping." : ""),
                    FooterText = "CAD markup: open the DWG in AutoCAD and run SCRIPT with the .scr file in CAD_Markup, or XREF the _QC_markup file."
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the issue browser");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open the HTML report");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Open the output folder");
                var res = td.Show();
                if (res == TaskDialogResult.CommandLink1) IssueBrowserWindow.ShowFor(App.LastReportPath, App.Navigator);
                else if (res == TaskDialogResult.CommandLink2 && html != null) Process.Start(new ProcessStartInfo(html) { UseShellExecute = true });
                else if (res == TaskDialogResult.CommandLink3) Process.Start(new ProcessStartInfo(engine.OutputFolder) { UseShellExecute = true });

                // open the first QC plan so the user lands on the result
                var first = vis.ViewsByLevel.Values.FirstOrDefault();
                if (first != null && doc.GetElement(first) is View v) uidoc.RequestViewChange(v);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("CAD QC", "QC failed:\n\n" + ex.Message + "\n\n" + ex.InnerException?.Message);
                return Result.Failed;
            }
        }
    }
}
