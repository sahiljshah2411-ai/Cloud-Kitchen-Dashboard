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
}
