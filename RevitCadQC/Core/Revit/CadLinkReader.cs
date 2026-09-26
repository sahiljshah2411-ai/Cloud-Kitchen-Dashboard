using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitCadQC.Core.Revit
{
    /// <summary>A DWG/DXF already linked (or imported with a path) in the Revit model.</summary>
    public sealed class ModelCadLink
    {
        public string Path { get; set; }
        public string LevelName { get; set; }
        public string ViewName { get; set; }
        public double RotationDeg { get; set; }
        public double OffsetXmm { get; set; }
        public double OffsetYmm { get; set; }
        public bool FileExists => File.Exists(Path);
        public override string ToString() => $"{System.IO.Path.GetFileName(Path)} → {LevelName}" + (ViewName != null ? $" (view '{ViewName}')" : "");
    }

    /// <summary>
    /// Reads the CAD links in the model. Their placement is the team's own alignment of CAD to model, so it is
    /// handed to the QC as an exact candidate transform (auto-alignment still competes with it and wins if the
    /// link is placed badly).
    /// </summary>
    public static class CadLinkReader
    {
        public static List<ModelCadLink> Read(Document doc)
        {
            var res = new List<ModelCadLink>();
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
            foreach (var imp in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
            {
                try
                {
                    if (!(doc.GetElement(imp.GetTypeId()) is CADLinkType type)) continue;
                    var ext = ExternalFileUtils.IsExternalFileReference(doc, type.Id) ? type.GetExternalFileReference() : null;
                    if (ext == null) continue;
                    var path = ModelPathUtils.ConvertModelPathToUserVisiblePath(ext.GetAbsolutePath());
                    if (string.IsNullOrEmpty(path) ||
                        !(path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var t = imp.GetTotalTransform();
                    string levelName = null, viewName = null;
                    if (imp.ViewSpecific && doc.GetElement(imp.OwnerViewId) is ViewPlan vp)
                    {
                        levelName = vp.GenLevel?.Name;
                        viewName = vp.Name;
                    }
                    if (levelName == null && imp.LevelId != ElementId.InvalidElementId)
                        levelName = (doc.GetElement(imp.LevelId) as Level)?.Name;
                    if (levelName == null)
                    {
                        // model link without a level: the highest level at or below the link origin
                        var z = t.Origin.Z;
                        levelName = levels.LastOrDefault(l => l.ProjectElevation <= z + 0.01)?.Name ?? levels.FirstOrDefault()?.Name;
                    }
                    if (levelName == null) continue;

                    res.Add(new ModelCadLink
                    {
                        Path = path,
                        LevelName = levelName,
                        ViewName = viewName,
                        RotationDeg = Math.Atan2(t.BasisX.Y, t.BasisX.X) * 180 / Math.PI,
                        OffsetXmm = t.Origin.X * 304.8,
                        OffsetYmm = t.Origin.Y * 304.8
                    });
                }
                catch { /* unusual import - skip it */ }
            }
            // one link per level + file
            return res.GroupBy(l => (l.LevelName, l.Path.ToLowerInvariant())).Select(g => g.First()).ToList();
        }
    }
}
