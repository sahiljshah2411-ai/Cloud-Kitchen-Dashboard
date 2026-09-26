using System;
using System.Collections.Generic;
using System.Linq;
using CadQC.Core.Dxf;
using CadQC.Core.Geometry;
using CadQC.Core.Model;
using CadQC.Core.Settings;

namespace CadQC.Core.Extraction
{
    /// <summary>Turns a (millimetre-scaled) CAD drawing, optionally clipped to one plan region, into QC elements.</summary>
    public sealed class CadExtractor
    {
        private readonly QcSettings _s;

        public CadExtractor(QcSettings settings) { _s = settings; }

        public CadFloorData Extract(CadDrawing dwg, Box2? region = null, string regionTitle = null)
        {
            var data = new CadFloorData { SourceFile = dwg.SourcePath, RegionTitle = regionTitle };
            Func<Vec2, bool> inRegion = p => region == null || region.Value.Contains(p);

            // ---------- walls ----------
            var wallLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var wallLines = new List<WallExtractor.SourceLine>();
            foreach (var c in dwg.Curves)
            {
                if (!IsWallLayer(c.Layer) || !IsNotOpeningBlock(dwg, c)) continue;
                foreach (var seg in c.Segments())
                {
                    if (!inRegion(seg.Mid)) continue;
                    wallLines.Add(new WallExtractor.SourceLine { Seg = seg, Layer = c.Layer, Handle = c.Handle });
                    wallLayers.Add(c.Layer);
                }
            }
            if (wallLines.Count == 0)
            {
                // No layer matched the wall standard: profile every layer and pick the ones that behave like walls.
                foreach (var layer in AutoDetectWallLayers(dwg, inRegion))
                {
                    wallLayers.Add(layer);
                    foreach (var c in dwg.Curves.Where(c => string.Equals(c.Layer, layer, StringComparison.OrdinalIgnoreCase)))
                        foreach (var seg in c.Segments())
                            if (inRegion(seg.Mid)) wallLines.Add(new WallExtractor.SourceLine { Seg = seg, Layer = c.Layer, Handle = c.Handle });
                }
                if (wallLayers.Count > 0)
                    data.Notes.Add("No layer matched the wall layer standard; auto-detected wall layers: " + string.Join(", ", wallLayers));
            }
            data.WallLayersUsed = wallLayers.OrderBy(x => x).ToList();
            data.WallLayerSegments = wallLines.Select(l => l.Seg).ToList();

            var wx = new WallExtractor(_s).Extract(wallLines);
            data.Walls = wx.Walls;
            data.UnpairedWallLines = wx.Unpaired;
            if (wx.DuplicateLines > 0) data.Notes.Add($"{wx.DuplicateLines} duplicate/overlapping lines found on wall layers.");
            WallExtractor.ApplyThicknessNotes(data.Walls, dwg.Texts.Where(t => inRegion(t.Position)), _s.AnnotationSearchRadius);

            // ---------- openings ----------
            data.Openings = ExtractOpenings(dwg, inRegion, data.Walls);

            // ---------- columns ----------
            data.Columns = ExtractColumns(dwg, inRegion);

            // ---------- grids ----------
            data.Grids = ExtractGrids(dwg, inRegion);

            // ---------- rooms ----------
            data.Rooms = ExtractRooms(dwg, inRegion, data.Grids);

            // ---------- dimensions ----------
            data.Dimensions = ExtractDimensions(dwg, inRegion);

            var b = Box2.Empty;
            foreach (var s in data.WallLayerSegments) b = b.Include(s.A).Include(s.B);
            data.Bounds = region ?? b;
            return data;
        }

        private bool IsWallLayer(string layer) => _s.Matches(_s.WallLayers, layer) && !_s.IsExcluded(layer)
                                                  && !_s.Matches(_s.DoorLayers, layer) && !_s.Matches(_s.WindowLayers, layer);

        /// <summary>Wall geometry that lives inside a door/window block is not a wall.</summary>
        private bool IsNotOpeningBlock(CadDrawing dwg, CadEntity e)
        {
            int idx = e.InsertIndex;
            int guard = 0;
            while (idx >= 0 && idx < dwg.Inserts.Count && guard++ < 20)
            {
                var ins = dwg.Inserts[idx];
                if (_s.Matches(_s.DoorBlockNames, ins.Name) || _s.Matches(_s.WindowBlockNames, ins.Name)) return false;
                idx = ins.InsertIndex;
            }
            return true;
        }

        #region wall layer auto-detection

        public List<string> AutoDetectWallLayers(CadDrawing dwg, Func<Vec2, bool> inRegion)
        {
            var res = new List<string>();
            var byLayer = dwg.Curves.Where(c => !_s.IsExcluded(c.Layer)).GroupBy(c => c.Layer, StringComparer.OrdinalIgnoreCase);
            foreach (var g in byLayer)
            {
                var segs = g.SelectMany(c => c.Segments()).Where(s => s.Length >= 300 && inRegion(s.Mid)).ToList();
                if (segs.Count < 8) continue;
                var wx = new WallExtractor(_s).Extract(segs.Select(s => new WallExtractor.SourceLine { Seg = s, Layer = g.Key }).ToList());
                double paired = wx.Walls.Sum(w => w.Length) * 2;
                double total = segs.Sum(s => s.Length);
                var thk = wx.Walls.Select(w => Math.Round(w.Thickness / 5) * 5).GroupBy(t => t).OrderByDescending(t => t.Count()).FirstOrDefault();
                // walls: most of the linework is paired and the spacings concentrate on a few thickness values
                if (total > 0 && paired / total > 0.55 && thk != null && thk.Count() >= 3) res.Add(g.Key);
            }
            return res;
        }

        #endregion

        #region openings

        private List<QcOpening> ExtractOpenings(CadDrawing dwg, Func<Vec2, bool> inRegion, List<QcWall> walls)
        {
            var list = new List<QcOpening>();
            var claimed = new HashSet<int>();
            int n = 0;

            for (int i = 0; i < dwg.Inserts.Count; i++)
            {
                var ins = dwg.Inserts[i];
                if (_s.IsExcluded(ins.Layer)) continue;
                bool doorish = _s.Matches(_s.DoorBlockNames, ins.Name) || _s.Matches(_s.DoorLayers, ins.Layer);
                bool windowish = _s.Matches(_s.WindowBlockNames, ins.Name) || _s.Matches(_s.WindowLayers, ins.Layer);
                if (!doorish && !windowish) continue;
                if (AncestorClaimed(dwg, ins, claimed)) continue; // nested leaf inside a door block
                if (ins.LocalBounds.IsEmpty) continue;

                var t = ins.Transform;
                var lb = ins.LocalBounds;
                var corners = new[] { new Vec2(lb.MinX, lb.MinY), new Vec2(lb.MaxX, lb.MinY), new Vec2(lb.MaxX, lb.MaxY), new Vec2(lb.MinX, lb.MaxY) }.Select(t.Apply).ToList();
                var center = t.Apply(lb.Center);
                if (!inRegion(center)) continue;

                double sx = t.ApplyVector(new Vec2(1, 0)).Length, sy = t.ApplyVector(new Vec2(0, 1)).Length;
                double extX = lb.Width * sx, extY = lb.Height * sy;
                double arcScale = t.UniformScale;
                var radii = ins.LocalArcRadii.Select(r => r * arcScale).Where(r => r >= 250 && r <= 2500).OrderByDescending(r => r).ToList();

                OpeningKind kind;
                if (doorish && !windowish) kind = OpeningKind.Door;
                else if (windowish && !doorish) kind = OpeningKind.Window;
                else kind = radii.Count > 0 ? OpeningKind.Door : OpeningKind.Window;

                double width;
                string widthSource;
                var attrW = ins.Attributes.Where(a => a.Key == "WIDTH" || a.Key == "W" || a.Key == "SIZE" || a.Key == "WD").Select(a => TextParsing.ParseLength(a.Value.Split('X', 'x')[0])).FirstOrDefault(v => v.HasValue && v > 200);
                if (attrW.HasValue) { width = attrW.Value; widthSource = "block attribute"; }
                else if (kind == OpeningKind.Door && radii.Count > 0)
                {
                    width = radii.Count >= 2 && radii[1] > 250 && radii[0] + radii[1] <= Math.Max(extX, extY) + 50 ? radii[0] + radii[1] : radii[0];
                    widthSource = radii.Count >= 2 && Math.Abs(width - radii[0]) > 1 ? "double swing arcs" : "swing arc radius";
                }
                else { width = Math.Max(extX, extY); widthSource = "block extents"; }

                double wallAng = ins.Rotation;
                var host = NearestParallelWall(walls, center, Math.Max(extX, extY), wallAng);
                Vec2 pos = center;
                if (host != null)
                {
                    wallAng = host.Centerline.Angle;
                    // project the block centre onto the wall line; for doors use the leaf chord, not the swing area
                    var cl = host.Centerline;
                    double tt = cl.Project(center);
                    pos = cl.PointAt(tt);
                    if (kind == OpeningKind.Door)
                    {
                        var along = corners.Select(c => cl.Project(c)).ToList();
                        double lo = along.Min(), hi = along.Max();
                        if (hi - lo > width * 0.6) pos = cl.PointAt((lo + hi) / 2);
                    }
                }

                claimed.Add(i);
                list.Add(new QcOpening
                {
                    Id = (kind == OpeningKind.Door ? "CD" : "CWN") + (++n).ToString("000"),
                    Kind = kind,
                    Position = pos,
                    Width = Math.Round(width, 1),
                    Angle = GeoMath.NormalizeUndirected(wallAng),
                    BlockName = ins.Name,
                    Layer = ins.Layer,
                    Handle = ins.Handle,
                    Tag = ins.Attributes.Where(a => a.Key == "TAG" || a.Key == "MARK" || a.Key == "NO" || a.Key == "TYPE").Select(a => a.Value).FirstOrDefault(),
                    WidthSource = widthSource,
                    HostWallId = host?.Id
                });
            }

            // loose door swings drawn without blocks
            var doorArcs = dwg.Arcs.Where(a => !a.IsCircle && _s.Matches(_s.DoorLayers, a.Layer) && !_s.IsExcluded(a.Layer)
                                              && a.Radius >= 400 && a.Radius <= 2000 && a.Sweep > 60 * GeoMath.Deg && a.Sweep < 120 * GeoMath.Deg
                                              && inRegion(a.Center) && !InsideClaimedInsert(dwg, a, claimed)).ToList();
            var leafs = new List<(Vec2 hinge, Vec2 closedEnd, double r, CadArc arc)>();
            foreach (var a in doorArcs)
            {
                var e1 = a.StartPoint; var e2 = a.EndPoint;
                double d1 = walls.Count > 0 ? walls.Min(w => w.Centerline.DistanceToPoint(a.Center + (e1 - a.Center) * 0.5)) : 0;
                double d2 = walls.Count > 0 ? walls.Min(w => w.Centerline.DistanceToPoint(a.Center + (e2 - a.Center) * 0.5)) : 0;
                leafs.Add((a.Center, d1 <= d2 ? e1 : e2, a.Radius, a));
            }
            var pairedLeaf = new bool[leafs.Count];
            for (int i = 0; i < leafs.Count; i++)
            {
                if (pairedLeaf[i]) continue;
                var L = leafs[i];
                int mate = -1;
                for (int j = i + 1; j < leafs.Count; j++)
                    if (!pairedLeaf[j] && leafs[j].closedEnd.DistanceTo(L.closedEnd) < 60 && leafs[j].hinge.DistanceTo(L.hinge) > L.r) { mate = j; break; }
                Vec2 pos; double width; string src;
                if (mate >= 0)
                {
                    pairedLeaf[mate] = true;
                    pos = (L.hinge + leafs[mate].hinge) * 0.5;
                    width = L.r + leafs[mate].r;
                    src = "double swing arcs";
                }
                else
                {
                    pos = (L.hinge + L.closedEnd) * 0.5;
                    width = L.r;
                    src = "swing arc radius";
                }
                var dir = mate >= 0 ? leafs[mate].hinge - L.hinge : L.closedEnd - L.hinge;
                list.Add(new QcOpening
                {
                    Id = "CD" + (++n).ToString("000"),
                    Kind = OpeningKind.Door,
                    Position = pos,
                    Width = Math.Round(width, 1),
                    Angle = GeoMath.NormalizeUndirected(Math.Atan2(dir.Y, dir.X)),
                    Layer = L.arc.Layer,
                    Handle = L.arc.Handle,
                    WidthSource = src
                });
            }

            // windows drawn as loose lines on window layers (no block)
            var winSegs = dwg.Curves.Where(c => c.InsertIndex < 0 && _s.Matches(_s.WindowLayers, c.Layer) && !_s.IsExcluded(c.Layer))
                                    .SelectMany(c => c.Segments().Select(s => (s, c))).Where(t => inRegion(t.s.Mid)).ToList();
            foreach (var cluster in ClusterSegments(winSegs.Select(t => t.s).ToList(), 15))
            {
                var pts = cluster.SelectMany(s => new[] { s.A, s.B }).ToList();
                var rect = GeoMath.MinAreaRect(pts);
                if (rect == null || rect.Max < 300 || rect.Max > 8000 || rect.Min > _s.MaxWallThickness + 60) continue;
                if (list.Any(o => o.Position.DistanceTo(rect.Center) < rect.Max / 2)) continue;
                double ang = rect.Width >= rect.Depth ? rect.Angle : rect.Angle + Math.PI / 2;
                list.Add(new QcOpening
                {
                    Id = "CWN" + (++n).ToString("000"),
                    Kind = OpeningKind.Window,
                    Position = rect.Center,
                    Width = Math.Round(rect.Max, 1),
                    Angle = GeoMath.NormalizeUndirected(ang),
                    Layer = winSegs.First(t => cluster.Contains(t.s)).c.Layer,
                    WidthSource = "window linework extents"
                });
            }

            AssignOpeningTags(dwg, inRegion, list);
            return list;
        }

        private static bool AncestorClaimed(CadDrawing dwg, CadInsert ins, HashSet<int> claimed)
        {
            int idx = ins.InsertIndex, guard = 0;
            while (idx >= 0 && guard++ < 20)
            {
                if (claimed.Contains(idx)) return true;
                idx = dwg.Inserts[idx].InsertIndex;
            }
            return false;
        }

        private static bool InsideClaimedInsert(CadDrawing dwg, CadEntity e, HashSet<int> claimed)
        {
            int idx = e.InsertIndex, guard = 0;
            while (idx >= 0 && guard++ < 20)
            {
                if (claimed.Contains(idx)) return true;
                idx = dwg.Inserts[idx].InsertIndex;
            }
            return false;
        }

        private QcWall NearestParallelWall(List<QcWall> walls, Vec2 p, double reach, double hintAngle)
        {
            QcWall best = null;
            double bd = double.MaxValue;
            foreach (var w in walls)
            {
                var cl = w.Centerline;
                double t = cl.Project(p);
                double outside = Math.Max(0, Math.Max(-t, t - cl.Length));
                if (outside > reach + 100) continue;
                double perp = Math.Abs(cl.SignedOffset(p));
                if (perp > Math.Max(reach, w.Thickness) + 100) continue;
                double score = perp + outside * 0.5 + (GeoMath.UndirectedDiff(cl.Angle, hintAngle) < 3 * GeoMath.Deg ? 0 : 200);
                if (score < bd) { bd = score; best = w; }
            }
            return best;
        }

        private void AssignOpeningTags(CadDrawing dwg, Func<Vec2, bool> inRegion, List<QcOpening> openings)
        {
            var tags = new List<(CadText t, string prefix, string tag)>();
            foreach (var t in dwg.Texts)
                if (inRegion(t.Position) && TextParsing.TryParseOpeningTag(t.Text, out var prefix, out var tag))
                    tags.Add((t, prefix, tag));
            var pairs = new List<(double d, int ti, QcOpening o)>();
            for (int i = 0; i < tags.Count; i++)
                foreach (var o in openings)
                {
                    double d = tags[i].t.Position.DistanceTo(o.Position);
                    if (d > Math.Max(1500, o.Width * 1.5)) continue;
                    bool kindOk = o.Kind == OpeningKind.Door ? TextParsing.TagPrefixIsDoor(tags[i].prefix) : TextParsing.TagPrefixIsWindow(tags[i].prefix);
                    pairs.Add((kindOk ? d : d + 2000, i, o));
                }
            var usedT = new HashSet<int>();
            var usedO = new HashSet<QcOpening>();
            foreach (var p in pairs.OrderBy(p => p.d))
            {
                if (usedT.Contains(p.ti) || usedO.Contains(p.o) || !string.IsNullOrEmpty(p.o.Tag)) continue;
                usedT.Add(p.ti); usedO.Add(p.o);
                p.o.Tag = tags[p.ti].tag;
            }
        }

        #endregion

        #region columns

        private List<QcColumn> ExtractColumns(CadDrawing dwg, Func<Vec2, bool> inRegion)
        {
            var cols = new List<QcColumn>();
            Func<CadEntity, bool> onColLayer = e => _s.Matches(_s.ColumnLayers, e.Layer) && !_s.IsExcluded(e.Layer);

            // closed outlines (polylines, hatch boundaries, solids)
            var loose = new List<Seg2>();
            foreach (var c in dwg.Curves.Where(x => onColLayer(x)))
            {
                bool closed = c.Closed || (c.Points.Count > 3 && c.Points[0].DistanceTo(c.Points[c.Points.Count - 1]) < 2);
                if (!closed)
                {
                    if (c.EntityType != "ARC") loose.AddRange(c.Segments());
                    continue;
                }
                if (c.EntityType == "CIRCLE") continue; // handled below
                AddColumnFromOutline(cols, c.Points, c.Layer, c.Handle, inRegion);
            }
            // loops made from individual lines
            foreach (var cluster in ClusterSegments(loose, 3))
            {
                if (cluster.Count < 3 || cluster.Count > 24) continue;
                AddColumnFromOutline(cols, cluster.SelectMany(s => new[] { s.A, s.B }).ToList(), dwg.Curves.FirstOrDefault(x => onColLayer(x))?.Layer, null, inRegion, fromLoose: true);
            }
            // circular columns
            foreach (var a in dwg.Arcs.Where(a => a.IsCircle && onColLayer(a) && a.Radius >= 75 && a.Radius <= 1500 && inRegion(a.Center)))
                cols.Add(new QcColumn { Center = a.Center, Width = a.Radius * 2, Depth = a.Radius * 2, IsCircular = true, Layer = a.Layer, Handle = a.Handle });
            // column blocks
            foreach (var ins in dwg.Inserts.Where(i => (_s.Matches(_s.ColumnBlockNames, i.Name) || onColLayer(i)) && !i.LocalBounds.IsEmpty))
            {
                var lb = ins.LocalBounds;
                var pts = new[] { new Vec2(lb.MinX, lb.MinY), new Vec2(lb.MaxX, lb.MinY), new Vec2(lb.MaxX, lb.MaxY), new Vec2(lb.MinX, lb.MaxY) }.Select(ins.Transform.Apply).ToList();
                AddColumnFromOutline(cols, pts, ins.Layer, ins.Handle, inRegion);
            }

            // de-duplicate (outline + hatch + block describing the same column)
            var res = new List<QcColumn>();
            foreach (var c in cols.OrderByDescending(c => c.Width * c.Depth))
            {
                if (res.Any(r => r.Center.DistanceTo(c.Center) < Math.Max(30, r.MinSize * 0.25))) continue;
                res.Add(c);
            }
            int k = 0;
            foreach (var c in res) c.Id = "CC" + (++k).ToString("000");
            return res;
        }

        private void AddColumnFromOutline(List<QcColumn> cols, List<Vec2> pts, string layer, string handle, Func<Vec2, bool> inRegion, bool fromLoose = false)
        {
            var rect = GeoMath.MinAreaRect(pts);
            if (rect == null || !inRegion(rect.Center)) return;
            if (rect.Min < 100 || rect.Max > 3000 || rect.Max / Math.Max(1, rect.Min) > 8) return;
            var hull = GeoMath.ConvexHull(pts);
            double fill = hull.Count >= 3 ? Math.Abs(GeoMath.PolygonArea(hull)) / (rect.Width * rect.Depth) : 0;
            if (fromLoose && fill < 0.85) return;
            // report width along the dominant axis so orientation can be compared
            cols.Add(new QcColumn
            {
                Center = rect.Center,
                Width = Math.Round(rect.Width, 1),
                Depth = Math.Round(rect.Depth, 1),
                Angle = rect.Angle,
                Layer = layer,
                Handle = handle,
                Outline = rect.Corners()
            });
        }

        /// <summary>Groups segments that touch (end points within tol) into connected clusters.</summary>
        public static List<List<Seg2>> ClusterSegments(List<Seg2> segs, double tol)
        {
            var parent = Enumerable.Range(0, segs.Count).ToArray();
            Func<int, int> find = null;
            find = x => parent[x] == x ? x : (parent[x] = find(parent[x]));
            var hash = new SpatialHash<int>(Math.Max(tol * 4, 50));
            for (int i = 0; i < segs.Count; i++)
            {
                foreach (var p in new[] { segs[i].A, segs[i].B })
                {
                    foreach (var hit in hash.Query(p, tol))
                    {
                        int a = find(i), b = find(hit.item);
                        if (a != b) parent[a] = b;
                    }
                }
                hash.Add(segs[i].A, i);
                hash.Add(segs[i].B, i);
            }
            return Enumerable.Range(0, segs.Count).GroupBy(find).Select(g => g.Select(i => segs[i]).ToList()).ToList();
        }

        #endregion

        #region grids

        private List<QcGrid> ExtractGrids(CadDrawing dwg, Func<Vec2, bool> inRegion)
        {
            Func<CadEntity, bool> onGrid = e => _s.Matches(_s.GridLayers, e.Layer) && !_s.IsExcluded(e.Layer);
            var lines = dwg.Curves.Where(c => onGrid(c) && c.EntityType != "CIRCLE" && c.EntityType != "ARC")
                .SelectMany(c => c.Segments().Select(s => (s, c.Layer, c.Handle)))
                .Where(t => t.s.Length > 150 && (inRegion(t.s.A) || inRegion(t.s.B))).ToList();

            // merge dashed collinear pieces
            var merged = new List<(Seg2 s, string layer, string handle)>();
            foreach (var t in lines.OrderByDescending(t => t.s.Length))
            {
                bool absorbed = false;
                for (int i = 0; i < merged.Count; i++)
                {
                    var m = merged[i].s;
                    if (GeoMath.UndirectedDiff(m.Angle, t.s.Angle) > 0.2 * GeoMath.Deg) continue;
                    if (Math.Abs(m.SignedOffset(t.s.A)) > 3 || Math.Abs(m.SignedOffset(t.s.B)) > 3) continue;
                    double ta = m.Project(t.s.A), tb = m.Project(t.s.B);
                    double lo = Math.Min(0, Math.Min(ta, tb)), hi = Math.Max(m.Length, Math.Max(ta, tb));
                    merged[i] = (new Seg2(m.PointAt(lo), m.PointAt(hi)), merged[i].layer, merged[i].handle);
                    absorbed = true;
                    break;
                }
                if (!absorbed) merged.Add(t);
            }
            merged = merged.Where(m => m.s.Length >= 2000).ToList();

            // bubbles: circles on grid layers + text inside, or attributed bubble blocks
            var bubbles = new List<(Vec2 c, double r, string name)>();
            foreach (var a in dwg.Arcs.Where(a => a.IsCircle && onGrid(a) && a.Radius >= 100 && a.Radius <= 1500))
            {
                var txt = dwg.Texts.Where(t => t.Position.DistanceTo(a.Center) <= a.Radius * 1.1 && t.Text.Length <= 5)
                                   .OrderBy(t => t.Position.DistanceTo(a.Center)).FirstOrDefault();
                if (txt != null) bubbles.Add((a.Center, a.Radius, txt.Text.Trim().ToUpperInvariant()));
            }
            foreach (var ins in dwg.Inserts.Where(i => onGrid(i) || i.Name.IndexOf("GRID", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                var name = ins.Attributes.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && v.Length <= 5);
                if (name == null) continue;
                var lb = ins.LocalBounds;
                double r = lb.IsEmpty ? 300 : Math.Max(lb.Width, lb.Height) * ins.Transform.UniformScale / 2;
                bubbles.Add((lb.IsEmpty ? ins.Position : ins.Transform.Apply(lb.Center), r, name.Trim().ToUpperInvariant()));
            }
            // plain text right at the end of a grid line on a grid layer
            foreach (var t in dwg.Texts.Where(t => onGrid(t) && t.Text.Trim().Length <= 4))
                if (!bubbles.Any(b => b.c.DistanceTo(t.Position) < b.r * 1.2))
                    bubbles.Add((t.Position, Math.Max(250, t.Height * 2), t.Text.Trim().ToUpperInvariant()));

            var grids = new List<QcGrid>();
            foreach (var m in merged)
            {
                string name = null;
                double best = double.MaxValue;
                foreach (var b in bubbles)
                {
                    double off = Math.Abs(m.s.SignedOffset(b.c));
                    if (off > b.r * 1.5) continue;
                    double t = m.s.Project(b.c);
                    double beyond = t < 0 ? -t : (t > m.s.Length ? t - m.s.Length : Math.Min(t, m.s.Length - t));
                    if (beyond > b.r * 4 + 600) continue;
                    double score = off + beyond * 0.2;
                    if (score < best) { best = score; name = b.name; }
                }
                grids.Add(new QcGrid { Id = "CG" + (grids.Count + 1), Name = name, Line = m.s, Layer = m.layer, Handle = m.handle });
            }
            // a grid line drawn in two pieces keeps one name
            return grids.GroupBy(g => g.Name ?? Guid.NewGuid().ToString()).Select(g => g.OrderByDescending(x => x.Line.Length).First()).ToList();
        }

        #endregion

        #region rooms

        private List<QcRoom> ExtractRooms(CadDrawing dwg, Func<Vec2, bool> inRegion, List<QcGrid> grids)
        {
            var rooms = new List<QcRoom>();
            var texts = dwg.Texts.Where(t => inRegion(t.Position) && !t.IsAttribute && !_s.IsExcluded(t.Layer)).ToList();
            var gridNames = new HashSet<string>(grids.Where(g => g.Name != null).Select(g => g.Name));
            foreach (var t in texts)
            {
                var txt = t.Text?.Trim();
                if (string.IsNullOrEmpty(txt) || txt.Length < 2 || txt.Length > 45) continue;
                if (gridNames.Contains(txt.ToUpperInvariant())) continue;
                if (TextParsing.TryParseOpeningTag(txt, out _, out _)) continue;
                if (TextParsing.ParseThicknessNote(txt) != null) continue;
                if (TextParsing.LooksLikePlanTitle(txt)) continue;
                if (!System.Text.RegularExpressions.Regex.IsMatch(txt, "[A-Za-z]{2,}")) continue;
                bool keyword = TextParsing.ContainsRoomKeyword(txt, _s.RoomKeywords);
                bool roomLayer = System.Text.RegularExpressions.Regex.IsMatch(t.Layer ?? "", "ROOM|AREA|SPACE", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!keyword && !roomLayer) continue;

                var room = new QcRoom { Name = StripSize(txt), Position = t.Position, Layer = t.Layer, Handle = t.Handle };
                // size / area in the same text or in a note just below
                string sizeSource = null;
                if (TextParsing.TryParseSize(txt, out double a, out double b)) sizeSource = txt;
                double near = Math.Max(1200, t.Height * 4);
                var below = texts.Where(o => o != t && o.Position.DistanceTo(t.Position) <= near && o.Position.Y <= t.Position.Y + t.Height * 0.2)
                                 .OrderBy(o => o.Position.DistanceTo(t.Position)).ToList();
                if (sizeSource == null)
                    foreach (var o in below)
                        if (TextParsing.TryParseSize(o.Text, out a, out b)) { sizeSource = o.Text; break; }
                if (sizeSource != null) { room.SizeA = Math.Round(a); room.SizeB = Math.Round(b); room.SizeText = sizeSource; }
                room.AreaM2 = TextParsing.ParseAreaM2(txt) ?? below.Select(o => TextParsing.ParseAreaM2(o.Text)).FirstOrDefault(v => v.HasValue);
                if (string.IsNullOrWhiteSpace(TextParsing.NormalizeRoomName(room.Name))) continue;
                rooms.Add(room);
            }
            // multi-line labels create near-duplicate entries: keep the first per spot and name
            var res = new List<QcRoom>();
            foreach (var r in rooms)
                if (!res.Any(x => x.Position.DistanceTo(r.Position) < 600 && TextParsing.RoomNamesEquivalent(x.Name, r.Name))) res.Add(r);
            int k = 0;
            foreach (var r in res) r.Id = "CR" + (++k).ToString("000");
            return res;
        }

        private static string StripSize(string s)
        {
            var cleaned = System.Text.RegularExpressions.Regex.Replace(s, @"\d+(\.\d+)?\s*('|""|MM|M)?\s*-?\s*\d*""?\s*[xX×*]\s*\d+(\.\d+)?\s*('|""|MM|M)?\s*-?\s*\d*""?", "");
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\d+(\.\d+)?\s*(SQ\.?\s*M\w*|SQM|M2|SQ\.?\s*FT|SFT)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return cleaned.Trim(' ', '-', ',', ':');
        }

        #endregion

        #region dimensions

        private List<QcDimension> ExtractDimensions(CadDrawing dwg, Func<Vec2, bool> inRegion)
        {
            var res = new List<QcDimension>();
            foreach (var d in dwg.Dimensions)
            {
                if (d.DimType != 0 && d.DimType != 1) continue;
                if (!inRegion(d.DefPoint1) && !inRegion(d.DefPoint2)) continue;
                Vec2 dir = d.DimType == 1 ? (d.DefPoint2 - d.DefPoint1).Normalized() : Vec2.FromAngle(d.Angle);
                if (dir.Length < 0.5) continue;
                double geom = Math.Abs((d.DefPoint2 - d.DefPoint1).Dot(dir));
                if (geom < 50) continue;
                double measured = d.Measurement ?? geom;
                double value = measured;
                bool overridden = false;
                var txt = d.TextOverride ?? "";
                if (txt.Length > 0 && !txt.Contains("<>"))
                {
                    var parsed = TextParsing.ParseLength(txt, smallNumbersAreMetres: false);
                    if (parsed.HasValue)
                    {
                        // text in metres on a mm drawing, e.g. "3.00"
                        if (parsed.Value < 50 && measured > 500) parsed = parsed.Value * 1000;
                        value = parsed.Value;
                        overridden = Math.Abs(parsed.Value - measured) > 0.6;
                    }
                }
                res.Add(new QcDimension
                {
                    P1 = d.DefPoint1,
                    P2 = d.DefPoint2,
                    Direction = dir,
                    ValueMm = value,
                    GeometricMm = geom,
                    Text = txt,
                    IsOverridden = overridden,
                    TextPosition = d.TextPosition,
                    Layer = d.Layer,
                    Handle = d.Handle
                });
            }
            return res;
        }

        #endregion
    }
}
