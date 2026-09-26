using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitCadQC.Core.Engine;
using RevitCadQC.Core.Report;
using RevitCadQC.Core.Settings;
using RevitCadQC.Core.Revit;
using RevitCadQC.UI;
using Newtonsoft.Json;

namespace RevitCadQC.Commands
{
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
}
