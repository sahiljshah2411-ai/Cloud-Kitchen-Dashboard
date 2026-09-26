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
}
