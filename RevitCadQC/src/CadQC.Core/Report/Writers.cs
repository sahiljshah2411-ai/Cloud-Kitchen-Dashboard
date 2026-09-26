using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;

namespace CadQC.Core.Report
{
    /// <summary>Carries issue status across runs: New / Open / Accepted, and adds issues that disappeared as Resolved.</summary>
    public static class IssueTracker
    {
        public static void Merge(QcReport previous, QcReport current)
        {
            if (previous == null) return;
            var prev = previous.Issues.Where(i => i.Status != IssueStatus.Resolved && i.Key != null)
                                      .GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First());
            var seen = new HashSet<string>();
            foreach (var i in current.Issues)
            {
                if (i.Key != null && prev.TryGetValue(i.Key, out var p))
                {
                    i.Status = p.Status == IssueStatus.Accepted ? IssueStatus.Accepted : IssueStatus.Open;
                    i.FirstSeenUtc = p.FirstSeenUtc;
                    i.Comment = p.Comment;
                    seen.Add(i.Key);
                }
            }
            // only floors that were checked this time can resolve issues
            var floors = new HashSet<string>(current.Floors.Select(f => f.LevelName));
            foreach (var p in prev.Values.Where(p => !seen.Contains(p.Key) && floors.Contains(p.LevelName)))
            {
                p.Status = IssueStatus.Resolved;
                current.Issues.Add(p);
            }
        }
    }

    public static class CsvReportWriter
    {
        public static void Write(QcReport r, string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Id,Status,Severity,Floor,Level,Category,Issue,Title,CAD value,Revit value,Delta (mm),Revit X (mm),Revit Y (mm),CAD X,CAD Y,Revit element ids,CAD handles,CAD layer,CAD file,Description,Comment");
            foreach (var i in r.Issues)
            {
                sb.AppendLine(string.Join(",", new[]
                {
                    i.Id, i.Status.ToString(), i.Severity.ToString(), i.Floor, i.LevelName, i.Category, i.IssueType, i.Title, i.CadValue, i.RevitValue,
                    F(i.Delta), F(i.RevitLocation?.X), F(i.RevitLocation?.Y), F(i.CadLocation?.X), F(i.CadLocation?.Y),
                    string.Join(" ", i.RevitElementIds), string.Join(" ", i.CadHandles), i.CadLayer, Path.GetFileName(i.CadFile ?? ""), i.Description, i.Comment
                }.Select(Esc)));
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static string F(double? v) => v.HasValue ? v.Value.ToString("0.#", CultureInfo.InvariantCulture) : "";

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }

    /// <summary>Minimal but real .xlsx writer (no external dependency): Summary, Issues and Floors sheets with styled headers and severity colours.</summary>
    public static class XlsxReportWriter
    {
        public static void Write(QcReport r, string path)
        {
            if (File.Exists(path)) File.Delete(path);
            using (var fs = new FileStream(path, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var sheets = new List<(string name, List<List<Cell>> rows, double[] widths)>
                {
                    ("Summary", SummaryRows(r), new double[] { 34, 60 }),
                    ("Issues", IssueRows(r), new double[] { 16, 10, 10, 18, 16, 14, 28, 60, 26, 30, 10, 12, 12, 24, 18, 80 }),
                    ("Floors", FloorRows(r), new double[] { 20, 18, 40, 30, 40, 12, 12, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10 })
                };
                Add(zip, "[Content_Types].xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                    "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                    string.Concat(sheets.Select((s, i) => $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")) +
                    "</Types>");
                Add(zip, "_rels/.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                Add(zip, "xl/workbook.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" +
                    string.Concat(sheets.Select((s, i) => $"<sheet name=\"{s.name}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) + "</sheets></workbook>");
                Add(zip, "xl/_rels/workbook.xml.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    string.Concat(sheets.Select((s, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>")) +
                    $"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
                Add(zip, "xl/styles.xml", Styles);
                for (int i = 0; i < sheets.Count; i++) Add(zip, $"xl/worksheets/sheet{i + 1}.xml", Sheet(sheets[i].rows, sheets[i].widths, i == 1));
            }
        }

        private struct Cell
        {
            public string Text;
            public double? Number;
            public int Style;
            public static implicit operator Cell(string s) => new Cell { Text = s };
        }

        // styles: 0 normal, 1 header, 2 critical, 3 major, 4 minor, 5 info, 6 title, 7 wrap
        private const string Styles =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"3\"><font><sz val=\"10\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"10\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"14\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"7\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1F3A5F\"/></patternFill></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF8D7DA\"/></patternFill></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFE5CC\"/></patternFill></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFFF6CC\"/></patternFill></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE2ECF7\"/></patternFill></fill></fills>" +
            "<borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf/></cellStyleXfs><cellXfs count=\"8\">" +
            "<xf fontId=\"0\" fillId=\"0\"/><xf fontId=\"1\" fillId=\"2\" applyFont=\"1\" applyFill=\"1\"/>" +
            "<xf fontId=\"0\" fillId=\"3\" applyFill=\"1\"/><xf fontId=\"0\" fillId=\"4\" applyFill=\"1\"/><xf fontId=\"0\" fillId=\"5\" applyFill=\"1\"/><xf fontId=\"0\" fillId=\"6\" applyFill=\"1\"/>" +
            "<xf fontId=\"2\" fillId=\"0\" applyFont=\"1\"/><xf fontId=\"0\" fillId=\"0\" applyAlignment=\"1\"><alignment wrapText=\"1\" vertical=\"top\"/></xf></cellXfs></styleSheet>";

        private static List<List<Cell>> SummaryRows(QcReport r)
        {
            var rows = new List<List<Cell>>
            {
                new List<Cell> { new Cell { Text = "Revit vs CAD Technical QC", Style = 6 } },
                new List<Cell> { "Project", r.ProjectName },
                new List<Cell> { "Revit model", r.RevitFile },
                new List<Cell> { "CAD folder", r.CadFolder },
                new List<Cell> { "Run (local time)", r.RunUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") },
                new List<Cell> { "Duration (s)", new Cell { Number = r.DurationSeconds } },
                new List<Cell> { "" },
                new List<Cell> { new Cell { Text = "Severity", Style = 1 }, new Cell { Text = "Open issues", Style = 1 } }
            };
            foreach (Severity s in Enum.GetValues(typeof(Severity)))
                rows.Add(new List<Cell> { new Cell { Text = s.ToString(), Style = SevStyle(s) }, new Cell { Number = r.Count(s) } });
            rows.Add(new List<Cell> { "Resolved since last run", new Cell { Number = r.Issues.Count(i => i.Status == IssueStatus.Resolved) } });
            rows.Add(new List<Cell> { "" });
            rows.Add(new List<Cell> { new Cell { Text = "Category", Style = 1 }, new Cell { Text = "Open issues", Style = 1 } });
            foreach (var g in r.Issues.Where(i => i.Status != IssueStatus.Resolved).GroupBy(i => i.Category).OrderByDescending(g => g.Count()))
                rows.Add(new List<Cell> { g.Key, new Cell { Number = g.Count() } });
            return rows;
        }

        private static List<List<Cell>> IssueRows(QcReport r)
        {
            var rows = new List<List<Cell>>
            {
                new[] { "Id", "Status", "Severity", "Floor", "Level", "Category", "Issue", "Title", "CAD value", "Revit value", "Delta (mm)", "Revit X", "Revit Y", "Revit ids", "CAD layer", "Description" }
                    .Select(h => new Cell { Text = h, Style = 1 }).ToList()
            };
            foreach (var i in r.Issues)
            {
                int st = SevStyle(i.Severity);
                rows.Add(new List<Cell>
                {
                    new Cell { Text = i.Id, Style = st }, i.Status.ToString(), new Cell { Text = i.Severity.ToString(), Style = st }, i.Floor, i.LevelName, i.Category, i.IssueType, i.Title,
                    i.CadValue, i.RevitValue, new Cell { Number = i.Delta }, new Cell { Number = i.RevitLocation.HasValue ? Math.Round(i.RevitLocation.Value.X, 1) : (double?)null },
                    new Cell { Number = i.RevitLocation.HasValue ? Math.Round(i.RevitLocation.Value.Y, 1) : (double?)null },
                    string.Join(" ", i.RevitElementIds), i.CadLayer, new Cell { Text = i.Description, Style = 7 }
                });
            }
            return rows;
        }

        private static List<List<Cell>> FloorRows(QcReport r)
        {
            var keys = new[] { "Walls", "Doors", "Windows", "Columns", "Rooms", "Grids" };
            var head = new List<string> { "Floor", "Revit level", "CAD file", "Plan title", "Alignment", "Match %", "RMS mm", "Issues" };
            head.AddRange(keys.SelectMany(k => new[] { "CAD " + k, "Revit " + k }));
            var rows = new List<List<Cell>> { head.Select(h => new Cell { Text = h, Style = 1 }).ToList() };
            foreach (var f in r.Floors)
            {
                var row = new List<Cell>
                {
                    f.Floor, f.LevelName, Path.GetFileName(f.CadFile ?? ""), f.RegionTitle ?? "", f.Alignment?.Method ?? "",
                    new Cell { Number = f.Alignment != null ? Math.Round(f.Alignment.InlierRatio * 100, 1) : (double?)null },
                    new Cell { Number = f.Alignment != null && f.Alignment.RmsMm < 1e9 ? Math.Round(f.Alignment.RmsMm, 1) : (double?)null },
                    new Cell { Number = f.IssueCount }
                };
                foreach (var k in keys)
                {
                    row.Add(new Cell { Number = f.CadCounts.TryGetValue(k, out var a) ? a : 0 });
                    row.Add(new Cell { Number = f.RevitCounts.TryGetValue(k, out var b) ? b : 0 });
                }
                rows.Add(row);
            }
            return rows;
        }

        private static int SevStyle(Severity s) => s == Severity.Critical ? 2 : s == Severity.Major ? 3 : s == Severity.Minor ? 4 : 5;

        private static string Sheet(List<List<Cell>> rows, double[] widths, bool freezeAndFilter)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            if (freezeAndFilter)
                sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
            sb.Append("<cols>");
            for (int i = 0; i < widths.Length; i++) sb.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{widths[i].ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
            sb.Append("</cols><sheetData>");
            for (int r = 0; r < rows.Count; r++)
            {
                sb.Append($"<row r=\"{r + 1}\">");
                for (int c = 0; c < rows[r].Count; c++)
                {
                    var cell = rows[r][c];
                    string refc = Col(c) + (r + 1);
                    string st = cell.Style != 0 ? $" s=\"{cell.Style}\"" : "";
                    if (cell.Number.HasValue)
                        sb.Append($"<c r=\"{refc}\"{st}><v>{cell.Number.Value.ToString(CultureInfo.InvariantCulture)}</v></c>");
                    else if (!string.IsNullOrEmpty(cell.Text))
                        sb.Append($"<c r=\"{refc}\"{st} t=\"inlineStr\"><is><t xml:space=\"preserve\">{Xml(cell.Text)}</t></is></c>");
                    else if (cell.Style != 0)
                        sb.Append($"<c r=\"{refc}\"{st}/>");
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData>");
            if (freezeAndFilter && rows.Count > 1) sb.Append($"<autoFilter ref=\"A1:{Col(rows[0].Count - 1)}{rows.Count}\"/>");
            sb.Append("</worksheet>");
            return sb.ToString();
        }

        private static string Col(int i)
        {
            string s = "";
            i++;
            while (i > 0) { int m = (i - 1) % 26; s = (char)('A' + m) + s; i = (i - 1) / 26; }
            return s;
        }

        private static string Xml(string s)
        {
            var clean = new string((s ?? "").Where(ch => ch == '\t' || ch == '\n' || ch == '\r' || ch >= 0x20).ToArray());
            return SecurityElement.Escape(clean);
        }

        private static void Add(ZipArchive zip, string name, string content)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(content);
        }
    }
}
