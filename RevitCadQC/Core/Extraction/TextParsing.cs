using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitCadQC.Core.Extraction
{
    /// <summary>Parsers for the text conventions found on architectural drawings (Indian, UK, US and metric practice).</summary>
    public static class TextParsing
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        #region floors

        private static readonly Dictionary<string, int> Ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            {"FIRST",1},{"SECOND",2},{"THIRD",3},{"FOURTH",4},{"FIFTH",5},{"SIXTH",6},{"SEVENTH",7},{"EIGHTH",8},{"NINTH",9},{"TENTH",10},
            {"ELEVENTH",11},{"TWELFTH",12},{"THIRTEENTH",13},{"FOURTEENTH",14},{"FIFTEENTH",15},{"SIXTEENTH",16},{"SEVENTEENTH",17},
            {"EIGHTEENTH",18},{"NINETEENTH",19},{"TWENTIETH",20}
        };

        public const int RoofIndex = 1000;

        /// <summary>
        /// Returns the floor indices a name refers to (0 = ground, -1 = basement 1, 1000 = terrace/roof).
        /// Handles "2ND FLOOR", "SECOND FLOOR PLAN", "L02", "LEVEL 2", "FF", "GF", "B1", "TERRACE",
        /// and typical-floor ranges like "TYPICAL FLOOR PLAN (3RD TO 7TH)".
        /// </summary>
        public static List<int> ParseFloors(string name)
        {
            var res = new List<int>();
            if (string.IsNullOrWhiteSpace(name)) return res;
            string s = " " + Regex.Replace(name.ToUpperInvariant().Replace('_', ' ').Replace('-', ' '), @"\s+", " ") + " ";

            // ranges: 3RD TO 7TH, 3-7 (after typical), 3RD & 5TH
            var range = Regex.Match(s, @"(\d{1,2})\s*(?:ST|ND|RD|TH)?\s*(?:TO|~|UPTO|UP TO|THRU|THROUGH)\s*(\d{1,2})\s*(?:ST|ND|RD|TH)?", Opt);
            if (range.Success && s.Contains("FLOOR") || range.Success && s.Contains("TYP"))
            {
                int a = int.Parse(range.Groups[1].Value, Inv), b = int.Parse(range.Groups[2].Value, Inv);
                for (int k = Math.Min(a, b); k <= Math.Max(a, b); k++) res.Add(k);
                return res;
            }

            if (Regex.IsMatch(s, @"\b(TERRACE|ROOF|OHT|OVERHEAD|MUMTY|HEAD ROOM|STAIR ROOM)\b", Opt)) { res.Add(RoofIndex); return res; }
            var bm = Regex.Match(s, @"\b(?:B|BASEMENT|CELLAR)\s*(\d)\b", Opt);
            if (bm.Success) { res.Add(-int.Parse(bm.Groups[1].Value, Inv)); return res; }
            if (Regex.IsMatch(s, @"\b(BASEMENT|CELLAR|LOWER GROUND|LGF|LG)\b", Opt)) { res.Add(-1); return res; }
            if (Regex.IsMatch(s, @"\b(GROUND|GF|G F|G\.F\.?|STILT|PLINTH|UGF|UPPER GROUND)\b", Opt)) { res.Add(0); return res; }

            foreach (var kv in Ordinals)
                if (Regex.IsMatch(s, @"\b" + kv.Key + @"\b", Opt)) { res.Add(kv.Value); return res; }

            var ord = Regex.Match(s, @"\b(\d{1,2})\s*(ST|ND|RD|TH)\b", Opt);
            if (ord.Success) { res.Add(int.Parse(ord.Groups[1].Value, Inv)); return res; }

            var lvl = Regex.Match(s, @"\b(?:LEVEL|LVL|LEV|FLOOR|FLR|STOREY|STORY|L|F)\s*[-.]?\s*(\d{1,3})\b", Opt);
            if (lvl.Success) { res.Add(int.Parse(lvl.Groups[1].Value, Inv)); return res; }

            var abbr = Regex.Match(s, @"\b(FF|SF|TF)\b", Opt);
            if (abbr.Success) { res.Add(abbr.Value.ToUpperInvariant() == "FF" ? 1 : abbr.Value.ToUpperInvariant() == "SF" ? 2 : 3); return res; }

            return res;
        }

        public static bool LooksLikePlanTitle(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 80) return false;
            var s = text.ToUpperInvariant();
            bool mentionsFloor = Regex.IsMatch(s, @"\b(FLOOR|LEVEL|BASEMENT|TERRACE|ROOF|STILT|GROUND)\b");
            bool mentionsPlan = s.Contains("PLAN") || s.Contains("LAYOUT");
            return mentionsFloor && (mentionsPlan || ParseFloors(s).Count > 0) && !s.Contains("FLOORING");
        }

        #endregion

        #region thickness notes

        private static readonly Regex ThkMm = new Regex(@"(?<![\d.])(\d{2,3})\s*(?:MM|mm)?\.?\s*(?:THK|THICK|TH\b|THK\.|WIDE)", Opt);
        private static readonly Regex ThkWall = new Regex(@"(?<![\d.])(\d{2,3})\s*(?:MM)?\s*(?:(?:THK\.?|THICK|BRICK|BLOCK|AAC|RCC|CONCRETE|MASONRY|PARTITION|GYPSUM|FLY\s*ASH|SOLID|HOLLOW|CMU)\s*)*WALL\b", Opt);
        private static readonly Regex ThkInch = new Regex(@"(?<![\d.])(\d{1,2}(?:\.\d)?|4½|4\s*1/2|13½)\s*(?:""|''|INCH|IN\b|INCHES)\s*(?:THK|THICK)?\s*(?:BRICK|BLOCK)?\s*(?:WALL)?", Opt);

        /// <summary>Parses a wall-thickness note like "230 THK", "230MM THK BRICK WALL", "115 WALL", 9" WALL. Returns mm.</summary>
        public static double? ParseThicknessNote(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 60) return null;
            var m = ThkMm.Match(text);
            if (m.Success) return double.Parse(m.Groups[1].Value, Inv);
            m = ThkWall.Match(text);
            if (m.Success) return double.Parse(m.Groups[1].Value, Inv);
            m = ThkInch.Match(text);
            if (m.Success && Regex.IsMatch(text, @"THK|THICK|WALL|BRICK|BLOCK", Opt))
            {
                string v = m.Groups[1].Value.Replace("½", ".5").Replace(" 1/2", ".5").Replace("1/2", ".5").Replace(" ", "");
                if (!double.TryParse(v, NumberStyles.Float, Inv, out double inch)) return null;
                return InchWallToMm(inch);
            }
            return null;
        }

        /// <summary>Conventional brick sizes (9" = 230, 4½" = 115) rather than exact conversions.</summary>
        public static double InchWallToMm(double inch)
        {
            if (Math.Abs(inch - 4.5) < 0.01) return 115;
            if (Math.Abs(inch - 9) < 0.01) return 230;
            if (Math.Abs(inch - 13.5) < 0.01) return 345;
            if (Math.Abs(inch - 6) < 0.01) return 150;
            if (Math.Abs(inch - 3) < 0.01) return 75;
            return Math.Round(inch * 25.4 / 5) * 5;
        }

        #endregion

        #region lengths, sizes, areas

        private static readonly Regex FeetInch = new Regex(@"^\s*(\d+)\s*'\s*-?\s*(\d+(?:\.\d+)?)?\s*(?:(\d+)/(\d+))?\s*""?\s*$", Opt);

        /// <summary>Parses a single length: "3000", "3.0" (m if &lt; 50 and has decimals), "10'-6"", "10' 6 1/2"". Returns mm.</summary>
        public static double? ParseLength(string s, bool smallNumbersAreMetres = true)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim().TrimEnd('.').Replace("MM", "").Replace("mm", "").Trim();
            var fi = FeetInch.Match(s);
            if (fi.Success && s.Contains("'"))
            {
                double ft = double.Parse(fi.Groups[1].Value, Inv);
                double inch = fi.Groups[2].Success && fi.Groups[2].Value.Length > 0 ? double.Parse(fi.Groups[2].Value, Inv) : 0;
                if (fi.Groups[3].Success && fi.Groups[3].Value.Length > 0)
                    inch += double.Parse(fi.Groups[3].Value, Inv) / double.Parse(fi.Groups[4].Value, Inv);
                return ft * 304.8 + inch * 25.4;
            }
            if (s.EndsWith("\"", StringComparison.Ordinal) && double.TryParse(s.TrimEnd('"'), NumberStyles.Float, Inv, out double onlyIn)) return onlyIn * 25.4;
            if (s.EndsWith("M", StringComparison.OrdinalIgnoreCase) && double.TryParse(s.Substring(0, s.Length - 1), NumberStyles.Float, Inv, out double metres)) return metres * 1000;
            if (double.TryParse(s, NumberStyles.Float, Inv, out double v))
            {
                if (smallNumbersAreMetres && v < 50 && s.Contains(".")) return v * 1000;
                return v;
            }
            return null;
        }

        private static readonly Regex SizeRx = new Regex(
            @"(\d+(?:\.\d+)?(?:\s*'\s*-?\s*\d*(?:\.\d+)?\s*""?)?)\s*(?:X|×|\*|BY)\s*(\d+(?:\.\d+)?(?:\s*'\s*-?\s*\d*(?:\.\d+)?\s*""?)?)", Opt);

        /// <summary>Parses a room size note such as "3000 X 3600", "3.0 x 3.6", 10'-0" X 12'-6". Returns mm.</summary>
        public static bool TryParseSize(string text, out double a, out double b)
        {
            a = b = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var m = SizeRx.Match(text);
            if (!m.Success) return false;
            var x = ParseLength(m.Groups[1].Value);
            var y = ParseLength(m.Groups[2].Value);
            if (x == null || y == null) return false;
            // bare integers below 100 are feet on Indian drawings ("10 X 12")
            if (x < 100 && !m.Groups[1].Value.Contains(".") && !m.Groups[1].Value.Contains("'")) x *= 304.8;
            if (y < 100 && !m.Groups[2].Value.Contains(".") && !m.Groups[2].Value.Contains("'")) y *= 304.8;
            if (x < 300 || y < 300 || x > 100000 || y > 100000) return false;
            a = x.Value; b = y.Value;
            return true;
        }

        private static readonly Regex AreaM2 = new Regex(@"(\d+(?:\.\d+)?)\s*(?:SQ\.?\s*M(?:T|TR|TRS)?\b|SQM\b|M2\b|M²|SQ\.?\s*METRE)", Opt);
        private static readonly Regex AreaFt2 = new Regex(@"(\d+(?:\.\d+)?)\s*(?:SQ\.?\s*FT\b|SFT\b|FT2\b|FT²|SQ\.?\s*FEET)", Opt);

        public static double? ParseAreaM2(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = AreaM2.Match(text);
            if (m.Success) return double.Parse(m.Groups[1].Value, Inv);
            m = AreaFt2.Match(text);
            if (m.Success) return double.Parse(m.Groups[1].Value, Inv) * 0.09290304;
            return null;
        }

        #endregion

        #region tags, names

        private static readonly Regex OpeningTag = new Regex(@"^\s*(D|DR|W|V|DW|WD|FD|SD|MD|FW|KW|GD|LD|RD|SL|SW|CW|MW|FDR|FFD|RS|ST|PD|WV|VW|OP)\s*[-.]?\s*(\d{1,3}[A-Z]?)\s*$", Opt);

        public static bool TryParseOpeningTag(string text, out string prefix, out string tag)
        {
            prefix = tag = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var m = OpeningTag.Match(text);
            if (!m.Success) return false;
            prefix = m.Groups[1].Value.ToUpperInvariant();
            tag = prefix + m.Groups[2].Value.ToUpperInvariant();
            return true;
        }

        public static string NormalizeTag(string s) =>
            string.IsNullOrWhiteSpace(s) ? "" : Regex.Replace(s.ToUpperInvariant(), @"[\s\-._/]", "");

        public static bool TagPrefixIsDoor(string prefix) =>
            prefix != null && (prefix.StartsWith("D") || prefix == "FD" || prefix == "SD" || prefix == "MD" || prefix == "GD" || prefix == "RS" || prefix == "PD" || prefix == "FFD" || prefix == "LD" || prefix == "RD");

        public static bool TagPrefixIsWindow(string prefix) =>
            prefix != null && (prefix.StartsWith("W") || prefix.StartsWith("V") || prefix == "FW" || prefix == "KW" || prefix == "SW" || prefix == "CW" || prefix == "MW");

        /// <summary>Canonical room name for comparison ("MASTER BED ROOM - 1" → "MASTER BEDROOM").</summary>
        public static string NormalizeRoomName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.ToUpperInvariant();
            s = SizeRx.Replace(s, " ");
            s = AreaM2.Replace(s, " ");
            s = AreaFt2.Replace(s, " ");
            s = Regex.Replace(s, @"[^A-Z ]", " ");
            s = Regex.Replace(s, @"\bBED\s+ROOM\b", "BEDROOM");
            s = Regex.Replace(s, @"\bTOI\b|\bW\s?C\b|\bWASHROOM\b|\bLAVATORY\b", "TOILET");
            s = Regex.Replace(s, @"\bBATH\b", "BATHROOM");
            s = Regex.Replace(s, @"\bBALC\b", "BALCONY");
            s = Regex.Replace(s, @"\bKIT\b|\bKITCH\b", "KITCHEN");
            s = Regex.Replace(s, @"\bM\s?BED\b|\bMBR\b", "MASTER BEDROOM");
            s = Regex.Replace(s, @"\bPOOJA\b", "PUJA");
            s = Regex.Replace(s, @"\bSTAIRCASE\b|\bSTAIRS\b", "STAIR");
            s = Regex.Replace(s, @"\bELEVATOR\b", "LIFT");
            s = Regex.Replace(s, @"\bCORRIDOR\b", "PASSAGE");
            s = Regex.Replace(s, @"\bSIT\s?OUT\b", "SITOUT");
            s = Regex.Replace(s, @"\bROOM\b(?!\s*\w)", "");
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        public static bool RoomNamesEquivalent(string a, string b)
        {
            var x = NormalizeRoomName(a);
            var y = NormalizeRoomName(b);
            if (x.Length == 0 || y.Length == 0) return false;
            if (x == y) return true;
            if (x.Contains(y) || y.Contains(x)) return true;
            var wx = new HashSet<string>(x.Split(' '));
            var wy = new HashSet<string>(y.Split(' '));
            int common = wx.Intersect(wy).Count();
            return common > 0 && common >= Math.Min(wx.Count, wy.Count);
        }

        public static bool ContainsRoomKeyword(string text, IEnumerable<string> keywords)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var s = " " + Regex.Replace(text.ToUpperInvariant(), @"[^A-Z0-9 ]", " ") + " ";
            foreach (var k in keywords)
                if (s.Contains(" " + k.ToUpperInvariant() + " ")) return true;
            return false;
        }

        /// <summary>Extracts the first number in a wall type name, e.g. "Brick 230mm" → 230.</summary>
        public static double? NumberInTypeName(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return null;
            var m = Regex.Match(typeName, @"(?<![\d.])(\d{2,3})(?:\s*mm)?(?![\d.])", RegexOptions.IgnoreCase);
            if (m.Success) return double.Parse(m.Groups[1].Value, Inv);
            var i = Regex.Match(typeName, @"(\d{1,2}(?:\.\d)?)\s*(?:""|in\b|inch)", RegexOptions.IgnoreCase);
            if (i.Success) return InchWallToMm(double.Parse(i.Groups[1].Value, Inv));
            return null;
        }

        #endregion
    }
}
