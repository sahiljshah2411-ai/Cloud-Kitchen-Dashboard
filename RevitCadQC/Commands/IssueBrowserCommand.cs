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
}
