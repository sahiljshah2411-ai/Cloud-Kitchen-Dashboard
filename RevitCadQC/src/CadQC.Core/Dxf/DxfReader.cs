using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CadQC.Core.Geometry;

namespace CadQC.Core.Dxf
{
    /// <summary>
    /// Self-contained ASCII DXF reader (R12 to 2018). It understands the entity types that carry
    /// architectural plan information and explodes block references recursively into world space:
    /// LINE, LWPOLYLINE, POLYLINE/VERTEX, ARC, CIRCLE, ELLIPSE, SOLID, TRACE, HATCH boundaries,
    /// TEXT, MTEXT, ATTRIB, INSERT (incl. MINSERT arrays, mirrored/OCS inserts) and DIMENSION.
    /// </summary>
    public sealed class DxfReader
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public bool SkipFrozenAndOffLayers { get; set; } = true;
        public int MaxBlockDepth { get; set; } = 12;

        private readonly Dictionary<string, BlockDef> _blocks = new Dictionary<string, BlockDef>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CadLayer> _layers = new Dictionary<string, CadLayer>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Box2> _blockBoundsCache = new Dictionary<string, Box2>(StringComparer.OrdinalIgnoreCase);
        private CadDrawing _dwg;

        public static CadDrawing Load(string path, bool skipHiddenLayers = true)
        {
            var r = new DxfReader { SkipFrozenAndOffLayers = skipHiddenLayers };
            return r.Read(path);
        }

        public CadDrawing Read(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(22, bytes.Length));
            if (head.StartsWith("AutoCAD Binary DXF", StringComparison.Ordinal))
                throw new InvalidDataException("Binary DXF is not supported. Re-export as ASCII DXF (the built-in converter does this automatically).");

            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { text = Encoding.GetEncoding("ISO-8859-1").GetString(bytes); }

            var dwg = ReadText(text);
            dwg.SourcePath = path;
            return dwg;
        }

        public CadDrawing ReadText(string text)
        {
            _dwg = new CadDrawing();
            var pairs = ReadPairs(text);
            int i = 0;
            var entities = new List<RawEntity>();
            while (i < pairs.Count)
            {
                if (pairs[i].Code == 0 && pairs[i].Value == "SECTION" && i + 1 < pairs.Count)
                {
                    string name = pairs[i + 1].Value.ToUpperInvariant();
                    i += 2;
                    int end = i;
                    while (end < pairs.Count && !(pairs[end].Code == 0 && pairs[end].Value == "ENDSEC")) end++;
                    switch (name)
                    {
                        case "HEADER": ParseHeader(pairs, i, end); break;
                        case "TABLES": ParseTables(pairs, i, end); break;
                        case "BLOCKS": ParseBlocks(pairs, i, end); break;
                        case "ENTITIES": entities = ParseEntityList(pairs, i, end); break;
                    }
                    i = end + 1;
                }
                else i++;
            }

            _dwg.UnitToMm = UnitsToMm(_dwg.InsUnits);
            foreach (var l in _layers.Values) _dwg.Layers.Add(l);

            foreach (var e in entities)
            {
                if (e.GetInt(67, 0) == 1) continue; // paper space
                Explode(e, Affine2.Identity, null, 0, null, -1);
            }
            return _dwg;
        }

        public static double UnitsToMm(int insUnits)
        {
            switch (insUnits)
            {
                case 1: return 25.4;
                case 2: return 304.8;
                case 4: return 1.0;
                case 5: return 10.0;
                case 6: return 1000.0;
                case 14: return 100.0;
                case 8: return 0.0000254; // microinches (rare)
                case 9: return 0.0254;    // mils
                case 10: return 914.4;    // yards
                default: return 1.0;      // unitless / unknown: resolved later by unit detection
            }
        }

        #region pairs

        private struct Pair
        {
            public int Code;
            public string Value;
        }

        private static List<Pair> ReadPairs(string text)
        {
            var list = new List<Pair>(text.Length / 12);
            using (var sr = new StringReader(text))
            {
                while (true)
                {
                    var c = sr.ReadLine();
                    if (c == null) break;
                    var v = sr.ReadLine();
                    if (v == null) break;
                    if (!int.TryParse(c.Trim(), NumberStyles.Integer, Inv, out int code)) continue;
                    list.Add(new Pair { Code = code, Value = code == 1 || code == 3 ? v.TrimEnd('\r') : v.Trim() });
                }
            }
            return list;
        }

        #endregion

        #region raw entities

        private sealed class RawEntity
        {
            public string Type;
            public readonly List<KeyValuePair<int, string>> Codes = new List<KeyValuePair<int, string>>();
            public readonly List<RawEntity> Children = new List<RawEntity>();

            public string Get(int code, string def = null)
            {
                foreach (var kv in Codes) if (kv.Key == code) return kv.Value;
                return def;
            }

            public double GetD(int code, double def = 0)
            {
                var s = Get(code);
                return s != null && double.TryParse(s, NumberStyles.Float, Inv, out double d) ? d : def;
            }

            public int GetInt(int code, int def = 0)
            {
                var s = Get(code);
                return s != null && int.TryParse(s, NumberStyles.Integer, Inv, out int d) ? d : def;
            }

            public bool Has(int code) => Codes.Any(k => k.Key == code);
            public string Layer => Get(8, "0");
        }

        private sealed class BlockDef
        {
            public string Name;
            public Vec2 Base;
            public int Flags;
            public List<RawEntity> Entities = new List<RawEntity>();
            public bool IsXref => (Flags & 4) != 0 || (Flags & 32) != 0;
        }

        private List<RawEntity> ParseEntityList(List<Pair> p, int start, int end)
        {
            var res = new List<RawEntity>();
            int i = start;
            while (i < end)
            {
                if (p[i].Code != 0) { i++; continue; }
                var e = new RawEntity { Type = p[i].Value.ToUpperInvariant() };
                i++;
                while (i < end && p[i].Code != 0) { e.Codes.Add(new KeyValuePair<int, string>(p[i].Code, p[i].Value)); i++; }

                bool hasChildren = e.Type == "POLYLINE" || (e.Type == "INSERT" && e.GetInt(66) == 1);
                if (hasChildren)
                {
                    while (i < end && p[i].Code == 0 && p[i].Value != "SEQEND")
                    {
                        var child = new RawEntity { Type = p[i].Value.ToUpperInvariant() };
                        if (child.Type != "VERTEX" && child.Type != "ATTRIB") break;
                        i++;
                        while (i < end && p[i].Code != 0) { child.Codes.Add(new KeyValuePair<int, string>(p[i].Code, p[i].Value)); i++; }
                        e.Children.Add(child);
                    }
                    if (i < end && p[i].Code == 0 && p[i].Value == "SEQEND")
                    {
                        i++;
                        while (i < end && p[i].Code != 0) i++;
                    }
                }
                res.Add(e);
            }
            return res;
        }

        private void ParseHeader(List<Pair> p, int start, int end)
        {
            for (int i = start; i < end; i++)
            {
                if (p[i].Code != 9) continue;
                string name = p[i].Value;
                if (i + 1 >= end) break;
                if (name == "$INSUNITS" && p[i + 1].Code == 70)
                    _dwg.InsUnits = int.TryParse(p[i + 1].Value, NumberStyles.Integer, Inv, out int u) ? u : 0;
                else if (name == "$ACADVER")
                    _dwg.DxfVersion = p[i + 1].Value;
            }
        }

        private void ParseTables(List<Pair> p, int start, int end)
        {
            var items = ParseEntityList(p, start, end);
            foreach (var e in items)
            {
                if (e.Type != "LAYER") continue;
                var name = e.Get(2);
                if (string.IsNullOrEmpty(name)) continue;
                int color = e.GetInt(62, 7);
                int flags = e.GetInt(70, 0);
                _layers[name] = new CadLayer { Name = name, Color = Math.Abs(color), Off = color < 0, Frozen = (flags & 1) != 0 };
            }
        }

        private void ParseBlocks(List<Pair> p, int start, int end)
        {
            int i = start;
            while (i < end)
            {
                if (p[i].Code == 0 && p[i].Value == "BLOCK")
                {
                    var hdr = new RawEntity { Type = "BLOCK" };
                    i++;
                    while (i < end && p[i].Code != 0) { hdr.Codes.Add(new KeyValuePair<int, string>(p[i].Code, p[i].Value)); i++; }
                    int bodyEnd = i;
                    while (bodyEnd < end && !(p[bodyEnd].Code == 0 && p[bodyEnd].Value == "ENDBLK")) bodyEnd++;
                    var def = new BlockDef
                    {
                        Name = hdr.Get(2) ?? hdr.Get(3) ?? "",
                        Base = new Vec2(hdr.GetD(10), hdr.GetD(20)),
                        Flags = hdr.GetInt(70),
                        Entities = ParseEntityList(p, i, bodyEnd)
                    };
                    if (!string.IsNullOrEmpty(def.Name)) _blocks[def.Name] = def;
                    i = bodyEnd + 1;
                }
                else i++;
            }
        }

        #endregion

        #region explode

        private bool LayerHidden(string layer)
        {
            if (!SkipFrozenAndOffLayers || layer == null) return false;
            return _layers.TryGetValue(layer, out var l) && (l.Frozen || l.Off);
        }

        private static Affine2 Ocs(RawEntity e)
        {
            // Arbitrary-axis algorithm for the only extrusion that matters in plan: (0,0,-1) mirrors X.
            return e.GetD(230, 1.0) < 0 ? Affine2.Scale(-1, 1) : Affine2.Identity;
        }

        private void Explode(RawEntity e, Affine2 xf, string parentLayer, int depth, string blockName, int insertIndex)
        {
            string layer = e.Layer;
            if (layer == "0" && parentLayer != null) layer = parentLayer;
            if (LayerHidden(layer) && e.Type != "INSERT") return;
            string handle = e.Get(5);

            try
            {
                switch (e.Type)
                {
                    case "LINE":
                        {
                            var a = xf.Apply(new Vec2(e.GetD(10), e.GetD(20)));
                            var b = xf.Apply(new Vec2(e.GetD(11), e.GetD(21)));
                            AddCurve(new List<Vec2> { a, b }, false, layer, handle, blockName, insertIndex, "LINE");
                            break;
                        }
                    case "LWPOLYLINE":
                        {
                            var ocs = xf.Compose(Ocs(e));
                            var pts = new List<Vec2>();
                            var bulges = new List<double>();
                            for (int k = 0; k < e.Codes.Count; k++)
                            {
                                var kv = e.Codes[k];
                                if (kv.Key == 10)
                                {
                                    double x = ParseD(kv.Value), y = 0;
                                    if (k + 1 < e.Codes.Count && e.Codes[k + 1].Key == 20) y = ParseD(e.Codes[k + 1].Value);
                                    pts.Add(new Vec2(x, y));
                                    bulges.Add(0);
                                }
                                else if (kv.Key == 42 && bulges.Count > 0) bulges[bulges.Count - 1] = ParseD(kv.Value);
                            }
                            bool closed = (e.GetInt(70) & 1) != 0;
                            var world = ExpandBulges(pts, bulges, closed).Select(ocs.Apply).ToList();
                            AddCurve(world, closed && !HasBulges(bulges), layer, handle, blockName, insertIndex, "LWPOLYLINE", closedFlag: closed);
                            break;
                        }
                    case "POLYLINE":
                        {
                            int flags = e.GetInt(70);
                            if ((flags & (16 | 64)) != 0) break; // 3D mesh / polyface
                            var ocs = (flags & 8) != 0 ? xf : xf.Compose(Ocs(e));
                            var pts = new List<Vec2>();
                            var bulges = new List<double>();
                            foreach (var v in e.Children.Where(c => c.Type == "VERTEX"))
                            {
                                if ((v.GetInt(70) & 16) != 0) continue; // spline frame control point
                                pts.Add(new Vec2(v.GetD(10), v.GetD(20)));
                                bulges.Add(v.GetD(42));
                            }
                            bool closed = (flags & 1) != 0;
                            var world = ExpandBulges(pts, bulges, closed).Select(ocs.Apply).ToList();
                            AddCurve(world, closed && !HasBulges(bulges), layer, handle, blockName, insertIndex, "POLYLINE", closedFlag: closed);
                            break;
                        }
                    case "ARC":
                    case "CIRCLE":
                        {
                            var ocs = xf.Compose(Ocs(e));
                            var c = new Vec2(e.GetD(10), e.GetD(20));
                            double r = e.GetD(40);
                            bool circle = e.Type == "CIRCLE";
                            double sa = circle ? 0 : e.GetD(50) * GeoMath.Deg;
                            double ea = circle ? 2 * Math.PI : e.GetD(51) * GeoMath.Deg;
                            AddArc(ocs, c, r, sa, ea, circle, layer, handle, blockName, insertIndex);
                            break;
                        }
                    case "ELLIPSE":
                        {
                            var c = new Vec2(e.GetD(10), e.GetD(20));
                            var major = new Vec2(e.GetD(11), e.GetD(21));
                            double ratio = e.GetD(40, 1);
                            double t0 = e.GetD(41, 0), t1 = e.GetD(42, 2 * Math.PI);
                            if (t1 <= t0) t1 += 2 * Math.PI;
                            var minor = major.Perp * ratio;
                            if (e.GetD(230, 1) < 0) minor = -minor;
                            int n = Math.Max(8, (int)((t1 - t0) / (10 * GeoMath.Deg)));
                            var pts = new List<Vec2>();
                            for (int k = 0; k <= n; k++)
                            {
                                double t = t0 + (t1 - t0) * k / n;
                                pts.Add(xf.Apply(c + major * Math.Cos(t) + minor * Math.Sin(t)));
                            }
                            AddCurve(pts, false, layer, handle, blockName, insertIndex, "ELLIPSE");
                            break;
                        }
                    case "SOLID":
                    case "TRACE":
                        {
                            var ocs = xf.Compose(Ocs(e));
                            // DXF SOLID vertex order is 1-2-4-3
                            var p1 = new Vec2(e.GetD(10), e.GetD(20));
                            var p2 = new Vec2(e.GetD(11), e.GetD(21));
                            var p3 = new Vec2(e.GetD(12), e.GetD(22));
                            var p4 = e.Has(13) ? new Vec2(e.GetD(13), e.GetD(23)) : p3;
                            var pts = new List<Vec2> { p1, p2, p4, p3 }.Distinct().Select(ocs.Apply).ToList();
                            AddCurve(pts, true, layer, handle, blockName, insertIndex, e.Type);
                            break;
                        }
                    case "HATCH":
                        ExplodeHatch(e, xf.Compose(Ocs(e)), layer, handle, blockName, insertIndex);
                        break;
                    case "TEXT":
                    case "ATTRIB":
                        {
                            if (e.Type == "ATTRIB" && (e.GetInt(70) & 1) != 0) break; // invisible attribute
                            var ocs = xf.Compose(Ocs(e));
                            bool aligned = e.GetInt(72) != 0 || e.GetInt(74) != 0 || e.GetInt(73) != 0;
                            var pos = aligned && e.Has(11) ? new Vec2(e.GetD(11), e.GetD(21)) : new Vec2(e.GetD(10), e.GetD(20));
                            double rot = e.GetD(50) * GeoMath.Deg;
                            var dir = ocs.ApplyVector(Vec2.FromAngle(rot));
                            _dwg.Texts.Add(new CadText
                            {
                                Text = CleanText(e.Get(1, "")),
                                Position = ocs.Apply(pos),
                                Height = e.GetD(40) * ocs.UniformScale,
                                Rotation = Math.Atan2(dir.Y, dir.X),
                                Layer = layer,
                                Handle = handle,
                                BlockName = blockName,
                                InsertIndex = insertIndex,
                                EntityType = e.Type,
                                IsAttribute = e.Type == "ATTRIB",
                                AttributeTag = e.Get(2)
                            });
                            break;
                        }
                    case "MTEXT":
                        {
                            var sb = new StringBuilder();
                            foreach (var kv in e.Codes.Where(k => k.Key == 3)) sb.Append(kv.Value);
                            sb.Append(e.Get(1, ""));
                            double rot = e.Has(11) ? Math.Atan2(e.GetD(21), e.GetD(11)) : e.GetD(50) * GeoMath.Deg;
                            var ocs = e.Has(11) ? xf : xf.Compose(Ocs(e));
                            var dir = ocs.ApplyVector(Vec2.FromAngle(rot));
                            _dwg.Texts.Add(new CadText
                            {
                                Text = CleanText(sb.ToString()),
                                Position = ocs.Apply(new Vec2(e.GetD(10), e.GetD(20))),
                                Height = e.GetD(40) * ocs.UniformScale,
                                Rotation = Math.Atan2(dir.Y, dir.X),
                                Layer = layer,
                                Handle = handle,
                                BlockName = blockName,
                                InsertIndex = insertIndex,
                                EntityType = "MTEXT"
                            });
                            break;
                        }
                    case "DIMENSION":
                        {
                            var ocs = xf; // definition points are WCS
                            int type = e.GetInt(70) & 0x0F;
                            string txt = e.Get(1, "");
                            _dwg.Dimensions.Add(new CadDimension
                            {
                                DimType = type,
                                DimLinePoint = ocs.Apply(new Vec2(e.GetD(10), e.GetD(20))),
                                TextPosition = ocs.Apply(new Vec2(e.GetD(11), e.GetD(21))),
                                DefPoint1 = ocs.Apply(new Vec2(e.GetD(13), e.GetD(23))),
                                DefPoint2 = ocs.Apply(new Vec2(e.GetD(14), e.GetD(24))),
                                Angle = Math.Atan2(ocs.ApplyVector(Vec2.FromAngle(e.GetD(50) * GeoMath.Deg)).Y,
                                                   ocs.ApplyVector(Vec2.FromAngle(e.GetD(50) * GeoMath.Deg)).X),
                                Measurement = e.Has(42) ? e.GetD(42) * ocs.UniformScale : (double?)null,
                                TextOverride = CleanText(txt),
                                Layer = layer,
                                Handle = handle,
                                BlockName = blockName,
                                InsertIndex = insertIndex,
                                EntityType = "DIMENSION"
                            });
                            break;
                        }
                    case "INSERT":
                        ExplodeInsert(e, xf, layer, depth, insertIndex);
                        break;
                }
            }
            catch (Exception ex)
            {
                _dwg.Warnings.Add($"Skipped {e.Type} (handle {handle}) on layer '{layer}': {ex.Message}");
            }
        }

        private void ExplodeInsert(RawEntity e, Affine2 xf, string layer, int depth, int insertIndex)
        {
            string name = e.Get(2);
            if (name == null) return;
            if (!_blocks.TryGetValue(name, out var def))
            {
                _dwg.Warnings.Add($"Block '{name}' referenced but not defined.");
                return;
            }
            if (def.IsXref)
            {
                if (!_dwg.Warnings.Any(w => w.Contains("'" + name + "'")))
                    _dwg.Warnings.Add($"External reference '{name}' is not bound; its geometry is not checked. Bind xrefs or add the xref DWG to the CAD folder.");
                return;
            }
            if (depth >= MaxBlockDepth) return;

            double sx = e.GetD(41, 1), sy = e.GetD(42, 1);
            double rot = e.GetD(50) * GeoMath.Deg;
            var pos = new Vec2(e.GetD(10), e.GetD(20));
            int cols = Math.Max(1, e.GetInt(70, 1)), rows = Math.Max(1, e.GetInt(71, 1));
            double cs = e.GetD(44), rs = e.GetD(45);
            var ocs = Ocs(e);
            bool hidden = LayerHidden(layer);

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    var local = Affine2.Translation(pos.X, pos.Y)
                        .Compose(Affine2.Rotation(rot))
                        .Compose(Affine2.Translation(c * cs, r * rs))
                        .Compose(Affine2.Scale(sx, sy))
                        .Compose(Affine2.Translation(-def.Base.X, -def.Base.Y));
                    var world = xf.Compose(ocs).Compose(local);

                    int myIndex = insertIndex;
                    if (!hidden)
                    {
                        var ins = new CadInsert
                        {
                            Name = name,
                            Layer = layer,
                            Handle = e.Get(5),
                            Position = xf.Compose(ocs).Apply(pos),
                            ScaleX = sx,
                            ScaleY = sy,
                            Rotation = world.RotationAngle,
                            Transform = world,
                            Depth = depth,
                            EntityType = "INSERT",
                            LocalBounds = BlockBounds(def, 0),
                            LocalArcRadii = BlockArcRadii(def, 0),
                            InsertIndex = insertIndex
                        };
                        foreach (var a in e.Children.Where(ch => ch.Type == "ATTRIB"))
                        {
                            var tag = a.Get(2);
                            if (!string.IsNullOrEmpty(tag)) ins.Attributes[tag.ToUpperInvariant()] = CleanText(a.Get(1, ""));
                        }
                        _dwg.Inserts.Add(ins);
                        myIndex = _dwg.Inserts.Count - 1;
                    }

                    foreach (var child in def.Entities)
                    {
                        if (child.Type == "ATTDEF") continue;
                        Explode(child, world, layer, depth + 1, name, myIndex);
                    }
                }
            }

            int attOwner = hidden ? insertIndex : _dwg.Inserts.Count - 1;
            foreach (var a in e.Children.Where(ch => ch.Type == "ATTRIB"))
                Explode(a, xf, layer, depth + 1, name, attOwner);
        }

        private Box2 BlockBounds(BlockDef def, int depth)
        {
            if (_blockBoundsCache.TryGetValue(def.Name, out var cached)) return cached;
            var b = Box2.Empty;
            if (depth > MaxBlockDepth) return b;
            var baseShift = Affine2.Translation(-def.Base.X, -def.Base.Y);
            foreach (var e in def.Entities)
            {
                switch (e.Type)
                {
                    case "LINE":
                        b = b.Include(baseShift.Apply(new Vec2(e.GetD(10), e.GetD(20))));
                        b = b.Include(baseShift.Apply(new Vec2(e.GetD(11), e.GetD(21))));
                        break;
                    case "LWPOLYLINE":
                        {
                            var ocs = baseShift.Compose(Ocs(e));
                            for (int k = 0; k < e.Codes.Count; k++)
                                if (e.Codes[k].Key == 10 && k + 1 < e.Codes.Count && e.Codes[k + 1].Key == 20)
                                    b = b.Include(ocs.Apply(new Vec2(ParseD(e.Codes[k].Value), ParseD(e.Codes[k + 1].Value))));
                            break;
                        }
                    case "POLYLINE":
                        foreach (var v in e.Children) b = b.Include(baseShift.Apply(new Vec2(v.GetD(10), v.GetD(20))));
                        break;
                    case "ARC":
                        {
                            var ocs = baseShift.Compose(Ocs(e));
                            var c = new Vec2(e.GetD(10), e.GetD(20));
                            double r = e.GetD(40);
                            foreach (var p in GeoMath.TessellateArc(c, r, e.GetD(50) * GeoMath.Deg, e.GetD(51) * GeoMath.Deg))
                                b = b.Include(ocs.Apply(p));
                            b = b.Include(ocs.Apply(c));
                            break;
                        }
                    case "CIRCLE":
                        {
                            var ocs = baseShift.Compose(Ocs(e));
                            var c = ocs.Apply(new Vec2(e.GetD(10), e.GetD(20)));
                            double r = e.GetD(40);
                            b = b.Include(new Vec2(c.X - r, c.Y - r)).Include(new Vec2(c.X + r, c.Y + r));
                            break;
                        }
                    case "INSERT":
                        {
                            if (!_blocks.TryGetValue(e.Get(2) ?? "", out var nd) || nd.IsXref || nd == def) break;
                            var nb = BlockBounds(nd, depth + 1);
                            if (nb.IsEmpty) break;
                            var t = baseShift.Compose(Ocs(e))
                                .Compose(Affine2.Translation(e.GetD(10), e.GetD(20)))
                                .Compose(Affine2.Rotation(e.GetD(50) * GeoMath.Deg))
                                .Compose(Affine2.Scale(e.GetD(41, 1), e.GetD(42, 1)));
                            b = b.Include(t.Apply(new Vec2(nb.MinX, nb.MinY))).Include(t.Apply(new Vec2(nb.MaxX, nb.MinY)))
                                 .Include(t.Apply(new Vec2(nb.MaxX, nb.MaxY))).Include(t.Apply(new Vec2(nb.MinX, nb.MaxY)));
                            break;
                        }
                }
            }
            _blockBoundsCache[def.Name] = b;
            return b;
        }

        private List<double> BlockArcRadii(BlockDef def, int depth)
        {
            var res = new List<double>();
            if (depth > 2) return res;
            foreach (var e in def.Entities)
            {
                if (e.Type == "ARC")
                {
                    double sweep = e.GetD(51) - e.GetD(50);
                    while (sweep <= 0) sweep += 360;
                    if (sweep > 20 && sweep < 200) res.Add(e.GetD(40));
                }
                else if (e.Type == "INSERT" && _blocks.TryGetValue(e.Get(2) ?? "", out var nd) && nd != def && !nd.IsXref)
                {
                    double s = Math.Sqrt(Math.Abs(e.GetD(41, 1) * e.GetD(42, 1)));
                    res.AddRange(BlockArcRadii(nd, depth + 1).Select(r => r * s));
                }
            }
            return res;
        }

        private void ExplodeHatch(RawEntity e, Affine2 xf, string layer, string handle, string blockName, int insertIndex)
        {
            var c = e.Codes;
            int i = c.FindIndex(k => k.Key == 91);
            if (i < 0) return;
            int pathCount = ParseI(c[i].Value);
            i++;
            for (int p = 0; p < pathCount && i < c.Count; p++)
            {
                while (i < c.Count && c[i].Key != 92) i++;
                if (i >= c.Count) return;
                int flags = ParseI(c[i].Value); i++;
                var pts = new List<Vec2>();
                if ((flags & 2) != 0)
                {
                    bool hasBulge = false, closed = true;
                    if (i < c.Count && c[i].Key == 72) { hasBulge = ParseI(c[i].Value) != 0; i++; }
                    if (i < c.Count && c[i].Key == 73) { closed = ParseI(c[i].Value) != 0; i++; }
                    int nv = (i < c.Count && c[i].Key == 93) ? ParseI(c[i++].Value) : 0;
                    var raw = new List<Vec2>();
                    var bulges = new List<double>();
                    for (int v = 0; v < nv && i < c.Count; v++)
                    {
                        double x = c[i].Key == 10 ? ParseD(c[i++].Value) : 0;
                        double y = i < c.Count && c[i].Key == 20 ? ParseD(c[i++].Value) : 0;
                        double bu = 0;
                        if (hasBulge && i < c.Count && c[i].Key == 42) bu = ParseD(c[i++].Value);
                        raw.Add(new Vec2(x, y)); bulges.Add(bu);
                    }
                    pts = ExpandBulges(raw, bulges, closed);
                }
                else
                {
                    int ne = (i < c.Count && c[i].Key == 93) ? ParseI(c[i++].Value) : 0;
                    for (int k = 0; k < ne && i < c.Count; k++)
                    {
                        if (c[i].Key != 72) return; // unexpected layout: stop safely
                        int et = ParseI(c[i++].Value);
                        switch (et)
                        {
                            case 1:
                                {
                                    var a = new Vec2(Next(c, ref i, 10), Next(c, ref i, 20));
                                    var b = new Vec2(Next(c, ref i, 11), Next(c, ref i, 21));
                                    if (pts.Count == 0 || pts[pts.Count - 1].DistanceTo(a) > 1e-6) pts.Add(a);
                                    pts.Add(b);
                                    break;
                                }
                            case 2:
                                {
                                    var cc = new Vec2(Next(c, ref i, 10), Next(c, ref i, 20));
                                    double r = Next(c, ref i, 40);
                                    double sa = Next(c, ref i, 50) * GeoMath.Deg, ea = Next(c, ref i, 51) * GeoMath.Deg;
                                    bool ccw = Next(c, ref i, 73) != 0;
                                    List<Vec2> arc;
                                    if (ccw) arc = GeoMath.TessellateArc(cc, r, sa, ea);
                                    else { arc = GeoMath.TessellateArc(cc, r, -ea, -sa); arc.Reverse(); } // clockwise edges store negated angles
                                    pts.AddRange(arc);
                                    break;
                                }
                            case 3:
                                {
                                    var cc = new Vec2(Next(c, ref i, 10), Next(c, ref i, 20));
                                    var major = new Vec2(Next(c, ref i, 11), Next(c, ref i, 21));
                                    double ratio = Next(c, ref i, 40);
                                    double sa = Next(c, ref i, 50) * GeoMath.Deg, ea = Next(c, ref i, 51) * GeoMath.Deg;
                                    Next(c, ref i, 73);
                                    if (ea <= sa) ea += 2 * Math.PI;
                                    for (int s = 0; s <= 12; s++)
                                    {
                                        double t = sa + (ea - sa) * s / 12;
                                        pts.Add(cc + major * Math.Cos(t) + major.Perp * ratio * Math.Sin(t));
                                    }
                                    break;
                                }
                            case 4:
                                {
                                    // spline: keep control points as an approximation
                                    Next(c, ref i, 94);
                                    Next(c, ref i, 73);
                                    Next(c, ref i, 74);
                                    int nk = (int)Next(c, ref i, 95);
                                    int ncp = (int)Next(c, ref i, 96);
                                    for (int q = 0; q < nk; q++) Next(c, ref i, 40);
                                    for (int q = 0; q < ncp; q++)
                                    {
                                        pts.Add(new Vec2(Next(c, ref i, 10), Next(c, ref i, 20)));
                                        if (i < c.Count && c[i].Key == 42) i++;
                                    }
                                    if (i < c.Count && c[i].Key == 97)
                                    {
                                        int nf = ParseI(c[i++].Value);
                                        for (int q = 0; q < nf; q++) { Next(c, ref i, 11); Next(c, ref i, 21); }
                                        if (i < c.Count && c[i].Key == 12) { i += 2; }
                                        if (i < c.Count && c[i].Key == 13) { i += 2; }
                                    }
                                    break;
                                }
                            default:
                                return;
                        }
                    }
                }
                if (pts.Count >= 2)
                    AddCurve(pts.Select(xf.Apply).ToList(), true, layer, handle, blockName, insertIndex, "HATCH");
                // skip source boundary object references
                if (i < c.Count && c[i].Key == 97)
                {
                    int ns = ParseI(c[i++].Value);
                    for (int q = 0; q < ns && i < c.Count && c[i].Key == 330; q++) i++;
                }
            }
        }

        private static double Next(List<KeyValuePair<int, string>> c, ref int i, int expected)
        {
            if (i < c.Count && c[i].Key == expected) return ParseD(c[i++].Value);
            return 0;
        }

        private void AddArc(Affine2 xf, Vec2 c, double r, double sa, double ea, bool circle, string layer, string handle, string blockName, int insertIndex)
        {
            var wc = xf.Apply(c);
            double scale = xf.UniformScale;
            double wsa, wea;
            if (circle) { wsa = 0; wea = 2 * Math.PI; }
            else
            {
                var ds = xf.ApplyVector(Vec2.FromAngle(sa));
                var de = xf.ApplyVector(Vec2.FromAngle(ea));
                wsa = Math.Atan2(ds.Y, ds.X);
                wea = Math.Atan2(de.Y, de.X);
                if (xf.IsMirrored) { var t = wsa; wsa = wea; wea = t; }
            }
            _dwg.Arcs.Add(new CadArc
            {
                Center = wc,
                Radius = r * scale,
                StartAngle = wsa,
                EndAngle = wea,
                IsCircle = circle,
                Layer = layer,
                Handle = handle,
                BlockName = blockName,
                InsertIndex = insertIndex,
                EntityType = circle ? "CIRCLE" : "ARC"
            });
            // keep a tessellated copy in Curves so walls drawn with arcs are also compared
            var pts = GeoMath.TessellateArc(c, r, sa, ea).Select(xf.Apply).ToList();
            AddCurve(pts, circle, layer, handle, blockName, insertIndex, circle ? "CIRCLE" : "ARC");
        }

        private void AddCurve(List<Vec2> pts, bool closed, string layer, string handle, string blockName, int insertIndex, string type, bool? closedFlag = null)
        {
            if (pts.Count < 2) return;
            _dwg.Curves.Add(new CadCurve
            {
                Points = pts,
                Closed = closedFlag ?? closed,
                Layer = layer,
                Handle = handle,
                BlockName = blockName,
                InsertIndex = insertIndex,
                EntityType = type
            });
        }

        private static bool HasBulges(List<double> b) => b.Any(x => Math.Abs(x) > 1e-9);

        private static List<Vec2> ExpandBulges(List<Vec2> pts, List<double> bulges, bool closed)
        {
            if (pts.Count == 0) return pts;
            var res = new List<Vec2> { pts[0] };
            int n = closed ? pts.Count : pts.Count - 1;
            for (int k = 0; k < n; k++)
            {
                var a = pts[k];
                var b = pts[(k + 1) % pts.Count];
                res.AddRange(GeoMath.BulgePoints(a, b, bulges[k]));
            }
            if (closed && res.Count > 1 && res[res.Count - 1].DistanceTo(res[0]) < 1e-9) res.RemoveAt(res.Count - 1);
            return res;
        }

        #endregion

        #region text

        private static readonly Regex MTextFormat = new Regex(@"\\[ACFHQTWfhqtwac][^;\\{}]*;|\\[LlOoKkNn]|\\p[^;]*;", RegexOptions.Compiled);
        private static readonly Regex MTextStack = new Regex(@"\\S([^;^/#]*)[\^/#]([^;]*);", RegexOptions.Compiled);

        public static string CleanText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\\P", " ").Replace("\\X", " ").Replace("\\~", " ");
            s = MTextStack.Replace(s, "$1/$2");
            s = MTextFormat.Replace(s, "");
            s = s.Replace("{", "").Replace("}", "");
            s = Regex.Replace(s, "%%[cC]", "Ø");
            s = Regex.Replace(s, "%%[dD]", "°");
            s = Regex.Replace(s, "%%[pP]", "±");
            s = Regex.Replace(s, "%%[uUoOkK]", "");
            s = Regex.Replace(s, @"\\U\+([0-9A-Fa-f]{4})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            s = s.Replace("\\\\", "\\");
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        private static double ParseD(string s) => double.TryParse(s, NumberStyles.Float, Inv, out double d) ? d : 0;
        private static int ParseI(string s) => int.TryParse(s.Trim(), NumberStyles.Integer, Inv, out int d) ? d : 0;

        #endregion
    }
}
