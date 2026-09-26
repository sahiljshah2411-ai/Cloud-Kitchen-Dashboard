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
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class VersionCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var dict = CadLayerDictionary.FindFile() ?? "(built-in rules)";
            var profiles = CadProfileStore.LoadAll();
            var oda = RevitCadQC.Core.Conversion.DwgConverter.FindOdaConverter() ?? RevitCadQC.Core.Conversion.DwgConverter.FindAcCoreConsole();
            var td = new TaskDialog("Revit CAD QC")
            {
                MainInstruction = ToolVersion.Display,
                ExpandedContent = string.Join("\n", ToolVersion.Notes),
                MainContent =
                    "Add-in:  " + typeof(App).Assembly.Location + "\n" +
                    "Revit:  " + data.Application.Application.VersionName + "\n\n" +
                    "DWG reading:  built in (ACadSharp)" + (oda != null ? "\nFallback converter:  " + oda : "") + "\n" +
                    "Layer dictionary:  " + dict + "\n" +
                    "Consultant profiles (" + profiles.Count + "):  " + CadProfileStore.Folder +
                    (profiles.Count > 0 ? "\n   " + string.Join(", ", profiles.Select(p => p.Name)) : "") + "\n" +
                    "Office default settings:  " + SettingsStore.DefaultPath + "\n" +
                    "Central rules (CadQcConfig.txt):  " + (CadQcConfig.FindFile() ?? "(none)") + "\n" +
                    "Team package:  " + (PackagePath.Current ?? "(not set - use Check for Update)")
            };
            td.Show();
            return Result.Succeeded;
        }
    }
}
