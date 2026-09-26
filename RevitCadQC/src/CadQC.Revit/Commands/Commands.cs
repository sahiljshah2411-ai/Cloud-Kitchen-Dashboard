using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CadQC.Core.Engine;
using CadQC.Core.Report;
using CadQC.Core.Settings;
using CadQC.Revit.Services;
using CadQC.Revit.UI;
using Newtonsoft.Json;

namespace CadQC.Revit.Commands
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
            var dlg = new SetupWindow(settings, levels, SettingsStore.PathFor(doc));
            new System.Windows.Interop.WindowInteropHelper(dlg) { Owner = data.Application.MainWindowHandle };
            if (dlg.ShowDialog() != true) return Result.Cancelled;
            SettingsStore.Save(doc, settings);

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

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class IssueBrowserCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var doc = data.Application.ActiveUIDocument?.Document;
            string path = App.LastReportPath;
            if ((path == null || !File.Exists(path)) && doc != null)
            {
                var s = SettingsStore.Load(doc);
                var outDir = !string.IsNullOrWhiteSpace(s.OutputFolder) ? s.OutputFolder : Path.Combine(s.CadFolder ?? "", "_RevitCadQC");
                path = Path.Combine(outDir, "qc_report.json");
            }
            IssueBrowserWindow.ShowFor(path, App.Navigator);
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class ClearQcCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;
            if (TaskDialog.Show("CAD QC", "Delete all 'QC - …' views (with their clouds and linked CAD) and clear QC comments?",
                    TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No) != TaskDialogResult.Yes) return Result.Cancelled;
            int n = RevitIssueVisualizer.Clear(doc);
            TaskDialog.Show("CAD QC", $"Removed {n} QC view(s).");
            return Result.Succeeded;
        }
    }

    /// <summary>Exports the model snapshot so QC can be re-run from the command line (CadQC.exe) without Revit.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class ExportSnapshotCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var doc = data.Application.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;
            var s = SettingsStore.Load(doc);
            var snap = new RevitExtractor(doc, s.CutPlaneHeight).Extract(s.IncludeLinkedModels);
            using (var dlg = new System.Windows.Forms.SaveFileDialog
            {
                Filter = "QC model snapshot (*.json)|*.json",
                FileName = (string.IsNullOrEmpty(doc.Title) ? "model" : Path.GetFileNameWithoutExtension(doc.Title)) + "_snapshot.json"
            })
            {
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return Result.Cancelled;
                File.WriteAllText(dlg.FileName, JsonConvert.SerializeObject(snap, Formatting.Indented));
                TaskDialog.Show("CAD QC", $"Snapshot saved: {snap.Walls.Count} walls, {snap.Openings.Count} doors/windows, {snap.Columns.Count} columns, {snap.Rooms.Count} rooms, {snap.Grids.Count} grids.");
            }
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class SettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var doc = data.Application.ActiveUIDocument?.Document;
            var path = SettingsStore.PathFor(doc);
            if (!File.Exists(path)) SettingsStore.Load(doc).Save(path);
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"")); }
            return Result.Succeeded;
        }
    }

    public sealed class ProjectOpenAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication app, CategorySet selected) =>
            app.ActiveUIDocument?.Document != null && !app.ActiveUIDocument.Document.IsFamilyDocument;
    }
}

namespace CadQC.Revit.Commands
{
    /// <summary>Review / edit the consultant layer profile for the project's CAD folder without running QC.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class LayerMappingCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var doc = data.Application.ActiveUIDocument?.Document;
            var settings = SettingsStore.Load(doc);
            var folder = settings.CadFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                folder = Ui.PickFolder(folder, "Folder with the CAD plans (DWG/DXF)");
                if (folder == null) return Result.Cancelled;
                settings.CadFolder = folder;
                SettingsStore.Save(doc, settings);
            }
            try
            {
                var scan = ProgressWindow.Run("Reading CAD layers", (p, ct) => LayerScanner.Scan(settings, p, ct));
                if (scan == null) return Result.Cancelled;
                var readable = scan.Where(f => f.Error == null).ToList();
                if (readable.Count == 0)
                {
                    TaskDialog.Show("CAD QC", "No readable DWG/DXF in\n" + folder + "\n\n" + string.Join("\n", scan.Select(f => Path.GetFileName(f.Path) + ": " + f.Error)));
                    return Result.Failed;
                }
                var name = readable.Select(f => f.Profile?.Name).FirstOrDefault(n => n != null) ?? new DirectoryInfo(folder).Name;
                var w = new LayerReviewWindow(LayerScanner.Merge(readable, settings.Layers), name, readable.Select(f => Path.GetFileName(f.Path)));
                new System.Windows.Interop.WindowInteropHelper(w) { Owner = data.Application.MainWindowHandle };
                w.ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("CAD QC", "Layer mapping failed:\n" + ex.Message);
                return Result.Failed;
            }
        }
    }

    /// <summary>Opens CadLayerDictionary.txt in Notepad (copies the shipped one to %APPDATA% first, so edits survive updates).</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class LayerDictionaryCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var user = Path.Combine(SettingsStore.AppDataFolder, CadLayerDictionary.FileName);
            if (!File.Exists(user))
            {
                var shipped = Path.Combine(Path.GetDirectoryName(typeof(App).Assembly.Location) ?? "", CadLayerDictionary.FileName);
                if (File.Exists(shipped)) File.Copy(shipped, user);
                else File.WriteAllText(user, "# Revit CAD QC layer dictionary\r\n# <CATEGORY>=<regex>|<regex>  - first match wins\r\nWALL=WALL\r\n");
            }
            Process.Start(new ProcessStartInfo("notepad.exe", "\"" + user + "\""));
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class VersionCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var dict = CadLayerDictionary.FindFile() ?? "(built-in rules)";
            var profiles = CadProfileStore.LoadAll();
            var oda = CadQC.Core.Conversion.DwgConverter.FindOdaConverter() ?? CadQC.Core.Conversion.DwgConverter.FindAcCoreConsole();
            var td = new TaskDialog("Revit CAD QC")
            {
                MainInstruction = "Revit CAD QC " + App.Version,
                MainContent =
                    "Add-in:  " + typeof(App).Assembly.Location + "\n" +
                    "Revit:  " + data.Application.Application.VersionName + "\n\n" +
                    "DWG reading:  built in (ACadSharp)" + (oda != null ? "\nFallback converter:  " + oda : "") + "\n" +
                    "Layer dictionary:  " + dict + "\n" +
                    "Consultant profiles (" + profiles.Count + "):  " + CadProfileStore.Folder +
                    (profiles.Count > 0 ? "\n   " + string.Join(", ", profiles.Select(p => p.Name)) : "") + "\n" +
                    "Office default settings:  " + SettingsStore.DefaultPath
            };
            td.Show();
            return Result.Succeeded;
        }
    }
}
