using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CadQC.Core.Geometry;

namespace CadQC.Core.Report
{
    /// <summary>
    /// Writes the QC result back into CAD, in the drawing's own coordinates and units:
    ///  • an R12 DXF markup (revision clouds, issue labels, missing/extra geometry and an optional Revit-model overlay)
    ///    that can be XREF'd/INSERTed over the original drawing (a DWG copy is produced when ODA is available);
    ///  • an AutoCAD script (.scr, AutoLISP entmake) that draws the same markup straight into the open DWG
    ///    and pre-selects the CAD entities involved.
    /// </summary>
    public static class CadMarkupWriter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static readonly (string name, int color)[] Layers =
        {
            ("QC_CRITICAL", 1), ("QC_MAJOR", 30), ("QC_MINOR", 2), ("QC_INFO", 4),
            ("QC_LABELS", 7), ("QC_REVIT_WALLS", 5), ("QC_REVIT_COLUMNS", 5), ("QC_REVIT_OPENINGS", 150)
        };

        public static string LayerFor(Severity s) => s == Severity.Critical ? "QC_CRITICAL" : s == Severity.Major ? "QC_MAJOR" : s == Severity.Minor ? "QC_MINOR" : "QC_INFO";

        public static void WriteDxf(string path, IList<QcIssue> issues, IList<FloorResult> floors, double unitToMm, bool includeRevitOverlay)
        {
            double k = 1.0 / (unitToMm <= 0 ? 1 : unitToMm);
            var sb = new StringBuilder();
            Action<int, string> P = (c, v) => { sb.Append(c).Append('\n').Append(v).Append('\n'); };
            Action<int, double> D = (c, v) => P(c, v.ToString("0.######", Inv));

            P(0, "SECTION"); P(2, "HEADER"); P(9, "$ACADVER"); P(1, "AC1009"); P(0, "ENDSEC");
            P(0, "SECTION"); P(2, "TABLES");
            P(0, "TABLE"); P(2, "LTYPE"); P(70, "2");
            P(0, "LTYPE"); P(2, "CONTINUOUS"); P(70, "0"); P(3, "Solid line"); P(72, "65"); P(73, "0"); D(40, 0);
            P(0, "LTYPE"); P(2, "QC_DASHED"); P(70, "0"); P(3, "__ __ __"); P(72, "65"); P(73, "2"); D(40, 300 * k); D(49, 200 * k); D(49, -100 * k);
            P(0, "ENDTAB");
            P(0, "TABLE"); P(2, "LAYER"); P(70, Layers.Length.ToString(Inv));
            foreach (var l in Layers) { P(0, "LAYER"); P(2, l.name); P(70, "0"); P(62, l.color.ToString(Inv)); P(6, "CONTINUOUS"); }
            P(0, "ENDTAB");
            P(0, "ENDSEC");
            P(0, "SECTION"); P(2, "ENTITIES");

            Action<string, IList<Vec2>, bool, double, string> Poly = (layer, pts, closed, width, ltype) =>
            {
                P(0, "POLYLINE"); P(8, layer); if (ltype != null) P(6, ltype); P(66, "1"); D(10, 0); D(20, 0); D(30, 0); P(70, closed ? "1" : "0");
                if (width > 0) { D(40, width); D(41, width); }
                foreach (var p in pts) { P(0, "VERTEX"); P(8, layer); D(10, p.X); D(20, p.Y); D(30, 0); }
                P(0, "SEQEND"); P(8, layer);
            };
            Action<string, Vec2, double, string> Text = (layer, p, h, s) =>
            {
                P(0, "TEXT"); P(8, layer); D(10, p.X); D(20, p.Y); D(30, 0); D(40, h); P(1, Ascii(s));
            };

            foreach (var i in issues.Where(x => x.CadLocation.HasValue))
            {
                string layer = LayerFor(i.Severity);
                var c = i.CadLocation.Value * k;
                double r = Math.Max(250, i.MarkerRadius) * k;
                // revision cloud: closed polyline with outward arcs
                var cloud = CloudPoints(c, r);
                P(0, "POLYLINE"); P(8, layer); P(66, "1"); D(10, 0); D(20, 0); D(30, 0); P(70, "1"); D(40, 25 * k); D(41, 25 * k);
                foreach (var p in cloud) { P(0, "VERTEX"); P(8, layer); D(10, p.X); D(20, p.Y); D(30, 0); D(42, 0.45); }
                P(0, "SEQEND"); P(8, layer);
                if (i.CadPath != null && i.CadPath.Count >= 2)
                    Poly(layer, i.CadPath.Select(p => p * k).ToList(), false, 40 * k, i.IssueType.StartsWith("Extra") ? "QC_DASHED" : null);
                double h = 180 * k;
                var tp = c + new Vec2(r * 0.75, r * 0.75);
                Text("QC_LABELS", tp, h, $"{i.Id} [{i.Severity}]");
                Text(layer, tp - new Vec2(0, h * 1.6), h * 0.8, Trunc(i.Title, 90));
                if (!string.IsNullOrEmpty(i.CadValue) || !string.IsNullOrEmpty(i.RevitValue))
                    Text(layer, tp - new Vec2(0, h * 3.0), h * 0.7, Trunc($"CAD: {i.CadValue ?? "-"} | Revit: {i.RevitValue ?? "-"}", 110));
            }

            if (includeRevitOverlay)
            {
                foreach (var f in floors.Where(f => f.Overlay != null && f.Alignment != null))
                {
                    var inv = f.Alignment.Transform.Inverse();
                    foreach (var w in f.Overlay.RevitWalls) Poly("QC_REVIT_WALLS", w.Select(p => inv.Apply(p) * k).ToList(), true, 0, null);
                    foreach (var col in f.Overlay.RevitColumns) Poly("QC_REVIT_COLUMNS", col.Select(p => inv.Apply(p) * k).ToList(), true, 0, null);
                    foreach (var o in f.Overlay.RevitOpenings)
                    {
                        var c = inv.Apply(o.p) * k;
                        double rr = Math.Max(100, o.w / 2) * k;
                        P(0, "CIRCLE"); P(8, "QC_REVIT_OPENINGS"); D(10, c.X); D(20, c.Y); D(30, 0); D(40, rr);
                    }
                }
            }

            P(0, "ENDSEC");
            P(0, "EOF");
            File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
        }

        /// <summary>AutoCAD script: open the original DWG, run SCRIPT, pick this file. Uses AutoLISP entmake so it is independent of OSNAP and command prompts.</summary>
        public static void WriteAutoCadScript(string path, IList<QcIssue> issues, double unitToMm)
        {
            double k = 1.0 / (unitToMm <= 0 ? 1 : unitToMm);
            var sb = new StringBuilder();
            sb.AppendLine("(vl-load-com)");
            sb.AppendLine("(setvar \"CMDECHO\" 0)");
            foreach (var l in Layers.Take(5))
                sb.AppendLine($"(if (not (tblsearch \"LAYER\" \"{l.name}\")) (entmake '((0 . \"LAYER\") (100 . \"AcDbSymbolTableRecord\") (100 . \"AcDbLayerTableRecord\") (2 . \"{l.name}\") (70 . 0) (62 . {l.color}) (6 . \"Continuous\"))))");
            foreach (var i in issues.Where(x => x.CadLocation.HasValue))
            {
                string layer = LayerFor(i.Severity);
                var c = i.CadLocation.Value * k;
                double r = Math.Max(250, i.MarkerRadius) * k;
                var cloud = CloudPoints(c, r);
                var v = new StringBuilder();
                foreach (var p in cloud) v.Append($" (10 {N(p.X)} {N(p.Y)}) (42 . 0.45)");
                sb.AppendLine($"(entmake '((0 . \"LWPOLYLINE\") (100 . \"AcDbEntity\") (8 . \"{layer}\") (100 . \"AcDbPolyline\") (90 . {cloud.Count}) (70 . 1) (43 . {N(25 * k)}){v}))");
                if (i.CadPath != null && i.CadPath.Count >= 2)
                {
                    var pv = string.Concat(i.CadPath.Select(p => $" (10 {N(p.X * k)} {N(p.Y * k)})"));
                    sb.AppendLine($"(entmake '((0 . \"LWPOLYLINE\") (100 . \"AcDbEntity\") (8 . \"{layer}\") (100 . \"AcDbPolyline\") (90 . {i.CadPath.Count}) (70 . 0) (43 . {N(40 * k)}){pv}))");
                }
                double h = 180 * k;
                var tp = c + new Vec2(r * 0.75, r * 0.75);
                sb.AppendLine(TextLisp("QC_LABELS", tp, h, $"{i.Id} [{i.Severity}]"));
                sb.AppendLine(TextLisp(layer, tp - new Vec2(0, h * 1.6), h * 0.8, Trunc(i.Title, 90)));
                if (!string.IsNullOrEmpty(i.CadValue) || !string.IsNullOrEmpty(i.RevitValue))
                    sb.AppendLine(TextLisp(layer, tp - new Vec2(0, h * 3.0), h * 0.7, Trunc($"CAD: {i.CadValue ?? "-"} | Revit: {i.RevitValue ?? "-"}", 110)));
            }
            // pre-select the original CAD entities involved in issues (non-destructive highlight)
            var handles = issues.SelectMany(i => i.CadHandles).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct().Take(3000).ToList();
            if (handles.Count > 0)
            {
                sb.AppendLine("(setq qcss (ssadd))");
                foreach (var chunk in Chunk(handles, 40))
                    sb.AppendLine("(foreach h '(" + string.Join(" ", chunk.Select(h => "\"" + h + "\"")) + ") (setq e (vl-catch-all-apply 'handent (list h))) (if (and e (not (vl-catch-all-error-p e)) (entget e)) (ssadd e qcss)))");
                sb.AppendLine("(if (> (sslength qcss) 0) (sssetfirst nil qcss))");
            }
            sb.AppendLine("(princ \"\\nRevit CAD QC markup added. Layers QC_CRITICAL / QC_MAJOR / QC_MINOR / QC_INFO. Entities with issues are selected.\")");
            sb.AppendLine("(princ)");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static string TextLisp(string layer, Vec2 p, double h, string s) =>
            $"(entmake (list '(0 . \"TEXT\") '(8 . \"{layer}\") '(10 {N(p.X)} {N(p.Y)} 0.0) '(40 . {N(h)}) (cons 1 \"{LispString(Ascii(s))}\")))";

        private static List<Vec2> CloudPoints(Vec2 c, double r)
        {
            int n = Math.Max(10, Math.Min(36, (int)(2 * Math.PI * r / (r * 0.35))));
            var pts = new List<Vec2>(n);
            for (int j = 0; j < n; j++) pts.Add(c + Vec2.FromAngle(2 * Math.PI * j / n) * r);
            return pts;
        }

        private static IEnumerable<List<string>> Chunk(List<string> l, int n)
        {
            for (int i = 0; i < l.Count; i += n) yield return l.Skip(i).Take(n).ToList();
        }

        private static string N(double v) => v.ToString("0.####", Inv);
        private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n - 1) + "~");
        private static string LispString(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        public static string Ascii(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("×", "x").Replace("²", "2").Replace("Ø", "DIA ").Replace("—", "-").Replace("–", "-").Replace("°", "deg").Replace("→", "->").Replace("…", "...");
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s) sb.Append(ch >= 32 && ch < 127 ? ch : '?');
            return sb.ToString();
        }
    }
}
