using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitCadQC.Core;
using RevitCadQC.Core.Settings;
using RevitCadQC.UI;

namespace RevitCadQC.Commands
{
    /// <summary>"Check for Update": pull the newest build from the team package folder. Restart Revit afterwards.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class RefreshCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            string year = data.Application.Application.VersionNumber;
            var pkg = PackagePath.Current;
            if (pkg == null)
            {
                pkg = Ui.PickFolder(null, "Team package folder (the one with version.txt, made by RUN_4_MAKE_TEAM_PACKAGE)");
                if (pkg == null) return Result.Cancelled;
                if (!File.Exists(Path.Combine(pkg, "version.txt")))
                {
                    TaskDialog.Show("CAD QC", "No version.txt in\n" + pkg + "\n\nPick the RevitCadQC_TEAM_PACKAGE folder.");
                    return Result.Failed;
                }
                PackagePath.Set(pkg);
            }
            pkg = UpdateCore.ResolvePackage(pkg);
            UpdateCore.RemoteInfo remote;
            try { remote = UpdateCore.ReadRemote(pkg); }
            catch (Exception ex) { TaskDialog.Show("CAD QC", "Cannot read the team package:\n" + pkg + "\n\n" + ex.Message); return Result.Failed; }
            if (remote == null) { TaskDialog.Show("CAD QC", "No version.txt in the team package:\n" + pkg); return Result.Failed; }

            bool newer = ToolVersion.IsNewer(remote.Version, ToolVersion.Version);
            var td = new TaskDialog("CAD QC - Check for Update")
            {
                MainInstruction = newer ? "New version " + remote.Version + " available" : "You have the latest version (" + ToolVersion.Version + ")",
                MainContent = "Installed: " + ToolVersion.Display + "\nPackage:   " + remote.Version + " " + remote.Date + "\n" + pkg +
                              (remote.Notes.Count > 0 ? "\n\n" + string.Join("\n", remote.Notes.Take(12)) : ""),
                CommonButtons = TaskDialogCommonButtons.Close
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, newer ? "Update now" : "Reinstall from the package anyway");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Use a different package folder");
            var r = td.Show();
            if (r == TaskDialogResult.CommandLink2)
            {
                var other = Ui.PickFolder(pkg, "Team package folder (with version.txt)");
                if (other != null) { PackagePath.Set(other); TaskDialog.Show("CAD QC", "Package folder set. Click Check for Update again."); }
                return Result.Succeeded;
            }
            if (r != TaskDialogResult.CommandLink1) return Result.Cancelled;

            int n = UpdateCore.CopyLatest(pkg, year, out string err);
            TaskDialog.Show("CAD QC", n > 0
                ? "Updated to " + remote.Version + ".\nRestart Revit to load it."
                : "Update failed:\n" + err);
            return n > 0 ? Result.Succeeded : Result.Failed;
        }
    }
}
