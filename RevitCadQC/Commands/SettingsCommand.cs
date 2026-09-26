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
}
