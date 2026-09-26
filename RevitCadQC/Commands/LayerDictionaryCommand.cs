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
}
