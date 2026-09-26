using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RevitCadQC.Core.Report;

namespace RevitCadQC.Core.Revit
{
    /// <summary>
    /// ExternalEvent handler used by the modeless issue browser: opens the right QC plan,
    /// zooms to the issue and selects the Revit elements involved.
    /// </summary>
    public sealed class IssueNavigator : IExternalEventHandler
    {
        private QcIssue _pending;
        private bool _selectOnly;
        private (ElementId view, XYZ min, XYZ max, List<ElementId> ids)? _afterActivate;
        private bool _hooked;

        public ExternalEvent Event { get; private set; }

        public static IssueNavigator Create()
        {
            var n = new IssueNavigator();
            n.Event = ExternalEvent.Create(n);
            return n;
        }

        public void Show(QcIssue issue, bool selectOnly = false)
        {
            _pending = issue;
            _selectOnly = selectOnly;
            Event.Raise();
        }

        public string GetName() => "CAD QC – go to issue";

        public void Execute(UIApplication app)
        {
            var issue = _pending;
            _pending = null;
            if (issue == null) return;
            var uidoc = app.ActiveUIDocument;
            if (uidoc == null) return;
            var doc = uidoc.Document;
            var ids = issue.RevitElementIds.Select(Parse).Where(id => id != ElementId.InvalidElementId && doc.GetElement(id) != null).ToList();
            if (_selectOnly || !issue.RevitLocation.HasValue)
            {
                if (ids.Count > 0) uidoc.Selection.SetElementIds(ids);
                if (ids.Count > 0 && !issue.RevitLocation.HasValue) uidoc.ShowElements(ids);
                return;
            }

            var view = FindView(doc, issue);
            double r = Math.Max(2500, issue.MarkerRadius * 5) / 304.8;
            double z = (view as ViewPlan)?.GenLevel?.ProjectElevation ?? 0;
            var c = new XYZ(issue.RevitLocation.Value.X / 304.8, issue.RevitLocation.Value.Y / 304.8, z);
            var min = new XYZ(c.X - r, c.Y - r, z);
            var max = new XYZ(c.X + r, c.Y + r, z);
            if (view == null)
            {
                if (ids.Count > 0) { uidoc.ShowElements(ids); uidoc.Selection.SetElementIds(ids); }
                return;
            }
            if (uidoc.ActiveView.Id == view.Id)
            {
                Zoom(uidoc, view.Id, min, max, ids);
            }
            else
            {
                _afterActivate = (view.Id, min, max, ids);
                if (!_hooked) { app.ViewActivated += OnViewActivated; _hooked = true; }
                uidoc.RequestViewChange(view);
            }
        }

        private void OnViewActivated(object sender, ViewActivatedEventArgs e)
        {
            if (_afterActivate == null || e.CurrentActiveView.Id != _afterActivate.Value.view) return;
            var a = _afterActivate.Value;
            _afterActivate = null;
            var uidoc = new UIDocument(e.Document);
            Zoom(uidoc, a.view, a.min, a.max, a.ids);
        }

        private static void Zoom(UIDocument uidoc, ElementId viewId, XYZ min, XYZ max, List<ElementId> ids)
        {
            var uiv = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == viewId);
            uiv?.ZoomAndCenterRectangle(min, max);
            if (ids.Count > 0) uidoc.Selection.SetElementIds(ids);
        }

        private static View FindView(Document doc, QcIssue issue)
        {
            var plans = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.GenLevel != null && v.GenLevel.Name == issue.LevelName).ToList();
            var qc = plans.Where(v => v.Name.StartsWith(RevitIssueVisualizer.ViewPrefix + issue.Floor, StringComparison.Ordinal))
                          .OrderByDescending(v => RevitExtractor.Id(v.Id).Length).ThenByDescending(v => RevitExtractor.Id(v.Id)).FirstOrDefault();
            return qc ?? plans.FirstOrDefault(v => v.ViewType == ViewType.FloorPlan);
        }

        private static ElementId Parse(string s)
        {
            if (string.IsNullOrEmpty(s) || !long.TryParse(s, out long v)) return ElementId.InvalidElementId;
#if REVIT2024 || REVIT2025 || REVIT2026 || REVIT2027
            return new ElementId(v);
#else
            return new ElementId((int)v);
#endif
        }
    }
}
