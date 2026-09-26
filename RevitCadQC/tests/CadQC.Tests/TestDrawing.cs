using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CadQC.Core.Geometry;
using CadQC.Core.Model;

namespace CadQC.Tests
{
    /// <summary>Tiny ASCII DXF author used to build synthetic floor plans for tests.</summary>
    public sealed class DxfBuilder
    {
        private readonly StringBuilder _ent = new StringBuilder();
        private readonly StringBuilder _blocks = new StringBuilder();
        private readonly HashSet<string> _layers = new HashSet<string> { "0" };
        public int InsUnits { get; set; } = 4;
        private int _handle = 0x100;

        private static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        private void P(StringBuilder sb, int c, string v) => sb.Append(c).Append('\n').Append(v).Append('\n');
        private string H() => (_handle++).ToString("X");

        public DxfBuilder Line(string layer, double x1, double y1, double x2, double y2, StringBuilder sb = null)
        {
            sb = sb ?? _ent; _layers.Add(layer);
            P(sb, 0, "LINE"); P(sb, 5, H()); P(sb, 8, layer); P(sb, 10, N(x1)); P(sb, 20, N(y1)); P(sb, 30, "0"); P(sb, 11, N(x2)); P(sb, 21, N(y2)); P(sb, 31, "0");
            return this;
        }

        public DxfBuilder Rect(string layer, double x1, double y1, double x2, double y2, StringBuilder sb = null)
        {
            sb = sb ?? _ent; _layers.Add(layer);
            P(sb, 0, "LWPOLYLINE"); P(sb, 5, H()); P(sb, 8, layer); P(sb, 90, "4"); P(sb, 70, "1");
            foreach (var p in new[] { (x1, y1), (x2, y1), (x2, y2), (x1, y2) }) { P(sb, 10, N(p.Item1)); P(sb, 20, N(p.Item2)); }
            return this;
        }

        public DxfBuilder Arc(string layer, double cx, double cy, double r, double a0, double a1, StringBuilder sb = null)
        {
            sb = sb ?? _ent; _layers.Add(layer);
            P(sb, 0, "ARC"); P(sb, 5, H()); P(sb, 8, layer); P(sb, 10, N(cx)); P(sb, 20, N(cy)); P(sb, 30, "0"); P(sb, 40, N(r)); P(sb, 50, N(a0)); P(sb, 51, N(a1));
            return this;
        }

        public DxfBuilder Circle(string layer, double cx, double cy, double r)
        {
            _layers.Add(layer);
            P(_ent, 0, "CIRCLE"); P(_ent, 5, H()); P(_ent, 8, layer); P(_ent, 10, N(cx)); P(_ent, 20, N(cy)); P(_ent, 30, "0"); P(_ent, 40, N(r));
            return this;
        }

        public DxfBuilder Text(string layer, double x, double y, double h, string text, double rotDeg = 0)
        {
            _layers.Add(layer);
            P(_ent, 0, "TEXT"); P(_ent, 5, H()); P(_ent, 8, layer); P(_ent, 10, N(x)); P(_ent, 20, N(y)); P(_ent, 30, "0"); P(_ent, 40, N(h)); P(_ent, 1, text); P(_ent, 50, N(rotDeg));
            return this;
        }

        public DxfBuilder Dim(string layer, double x1, double y1, double x2, double y2, double angleDeg, string text = "")
        {
            _layers.Add(layer);
            double meas = Math.Abs((x2 - x1) * Math.Cos(angleDeg * Math.PI / 180) + (y2 - y1) * Math.Sin(angleDeg * Math.PI / 180));
            P(_ent, 0, "DIMENSION"); P(_ent, 5, H()); P(_ent, 8, layer); P(_ent, 2, "*D1");
            P(_ent, 10, N(x2)); P(_ent, 20, N(y2 + 500)); P(_ent, 30, "0"); P(_ent, 11, N((x1 + x2) / 2)); P(_ent, 21, N(y2 + 600)); P(_ent, 31, "0");
            P(_ent, 70, "32"); P(_ent, 1, text); P(_ent, 42, N(meas));
            P(_ent, 13, N(x1)); P(_ent, 23, N(y1)); P(_ent, 33, "0"); P(_ent, 14, N(x2)); P(_ent, 24, N(y2)); P(_ent, 34, "0"); P(_ent, 50, N(angleDeg));
            return this;
        }

        public StringBuilder BeginBlock(string name)
        {
            var sb = new StringBuilder();
            P(sb, 0, "BLOCK"); P(sb, 8, "0"); P(sb, 2, name); P(sb, 70, "0"); P(sb, 10, "0"); P(sb, 20, "0"); P(sb, 30, "0"); P(sb, 3, name);
            return sb;
        }

        public void EndBlock(StringBuilder sb)
        {
            P(sb, 0, "ENDBLK"); P(sb, 8, "0");
            _blocks.Append(sb);
        }

        public DxfBuilder Insert(string layer, string block, double x, double y, double rotDeg = 0, double sx = 1, double sy = 1)
        {
            _layers.Add(layer);
            P(_ent, 0, "INSERT"); P(_ent, 5, H()); P(_ent, 8, layer); P(_ent, 2, block); P(_ent, 10, N(x)); P(_ent, 20, N(y)); P(_ent, 30, "0");
            P(_ent, 41, N(sx)); P(_ent, 42, N(sy)); P(_ent, 50, N(rotDeg));
            return this;
        }

        public string Build()
        {
            var sb = new StringBuilder();
            P(sb, 0, "SECTION"); P(sb, 2, "HEADER"); P(sb, 9, "$ACADVER"); P(sb, 1, "AC1015"); P(sb, 9, "$INSUNITS"); P(sb, 70, InsUnits.ToString()); P(sb, 0, "ENDSEC");
            P(sb, 0, "SECTION"); P(sb, 2, "TABLES"); P(sb, 0, "TABLE"); P(sb, 2, "LAYER"); P(sb, 70, _layers.Count.ToString());
            foreach (var l in _layers) { P(sb, 0, "LAYER"); P(sb, 2, l); P(sb, 70, "0"); P(sb, 62, "7"); P(sb, 6, "CONTINUOUS"); }
            P(sb, 0, "ENDTAB"); P(sb, 0, "ENDSEC");
            P(sb, 0, "SECTION"); P(sb, 2, "BLOCKS"); sb.Append(_blocks); P(sb, 0, "ENDSEC");
            P(sb, 0, "SECTION"); P(sb, 2, "ENTITIES"); sb.Append(_ent); P(sb, 0, "ENDSEC");
            P(sb, 0, "EOF");
            return sb.ToString();
        }
    }

    /// <summary>
    /// A 10 m × 8 m second-floor flat with known, deliberate CAD↔Revit differences.
    ///
    ///   CAD                                     Revit (rotated 30°, moved 100 m / -50 m)
    ///   exterior 230 walls, window 1200 bottom  exterior 230 walls, NO window
    ///   wall A x=5000, 115 thk, door 900        wall A 115, door 1000 wide
    ///   wall B y=4000, 230 thk + "230 THK" note wall B modelled 200 thick
    ///   wall C y=6000 (x 5057..9770), 115 thk   wall C NOT modelled
    ///   —                                       extra wall x=7500 (y 115..3000)
    ///   columns 300×450 @ (2500,2000), 300×300 @ (7500,2000)  second column 60 mm off
    ///   rooms BEDROOM (+size note), KITCHEN     BEDROOM, DINING (name mismatch)
    ///   grids 1,2,A,B                           same
    ///   dimension across wall B faces = 230     Revit faces 200 apart
    /// </summary>
    public static class TestPlan
    {
        public static readonly Similarity2 CadToRevit = new Similarity2 { Rotation = 30 * GeoMath.Deg, Tx = 100000, Ty = -50000 };

        public static string BuildDxf(double unitScale = 1.0)
        {
            double k = 1.0 / unitScale;
            var d = new DxfBuilder { InsUnits = unitScale == 1.0 ? 4 : 0 };
            Func<double, double> S = v => v * k;
            const string W = "A-WALL";

            // exterior walls: bottom has a window gap 7000..8200
            d.Line(W, S(0), 0, S(7000), 0).Line(W, S(8200), 0, S(10000), 0);
            d.Line(W, S(230), S(230), S(7000), S(230)).Line(W, S(8200), S(230), S(9770), S(230));
            d.Line(W, S(10000), 0, S(10000), S(8000)).Line(W, S(9770), S(230), S(9770), S(7770));
            d.Line(W, S(10000), S(8000), 0, S(8000)).Line(W, S(9770), S(7770), S(230), S(7770));
            d.Line(W, 0, S(8000), 0, 0).Line(W, S(230), S(7770), S(230), S(230));
            // wall A (vertical, 115) with door gap 1000..1900
            d.Line(W, S(4942.5), S(230), S(4942.5), S(1000)).Line(W, S(4942.5), S(1900), S(4942.5), S(7770));
            d.Line(W, S(5057.5), S(230), S(5057.5), S(1000)).Line(W, S(5057.5), S(1900), S(5057.5), S(7770));
            // wall B (horizontal, 230) from exterior to wall A
            d.Line(W, S(230), S(3885), S(4942.5), S(3885)).Line(W, S(230), S(4115), S(4942.5), S(4115));
            d.Text("A-ANNO-TEXT", S(1500), S(4250), S(120), "230 THK. BRICK WALL");
            // wall C (horizontal, 115) from wall A to exterior
            d.Line(W, S(5057.5), S(5942.5), S(9770), S(5942.5)).Line(W, S(5057.5), S(6057.5), S(9770), S(6057.5));

            // door block: leaf + 90° swing, width 900, hinge at origin, opening along +X
            var door = d.BeginBlock("DOOR_SINGLE");
            d.Line("0", 0, 0, 0, 900, door);
            d.Arc("0", 0, 0, 900, 0, 90, door);
            d.EndBlock(door);
            // placed in wall A: hinge at (5000,1000), opening along +Y → rotate 90°
            d.Insert("A-DOOR", "DOOR_SINGLE", S(5000), S(1000), 90, k, k);
            d.Text("A-DOOR-IDEN", S(5300), S(1450), S(150), "D1");

            // window block 1200 × 230 centred on origin
            var win = d.BeginBlock("WIN_1200");
            d.Line("0", -600, -115, 600, -115, win).Line("0", -600, 0, 600, 0, win).Line("0", -600, 115, 600, 115, win);
            d.Line("0", -600, -115, -600, 115, win).Line("0", 600, -115, 600, 115, win);
            d.EndBlock(win);
            d.Insert("A-GLAZ", "WIN_1200", S(7600), S(115), 0, k, k);
            d.Text("A-GLAZ-IDEN", S(7600), S(-500), S(150), "W1");

            // columns
            d.Rect("S-COLS", S(2350), S(1775), S(2650), S(2225));
            d.Rect("S-COLS", S(7350), S(1850), S(7650), S(2150));

            // grids with bubbles
            d.Line("S-GRID", S(115), S(-2000), S(115), S(10000)).Circle("S-GRID", S(115), S(10400), S(400)).Text("S-GRID", S(115), S(10400), S(300), "1");
            d.Line("S-GRID", S(9885), S(-2000), S(9885), S(10000)).Circle("S-GRID", S(9885), S(10400), S(400)).Text("S-GRID", S(9885), S(10400), S(300), "2");
            d.Line("S-GRID", S(-2000), S(115), S(12000), S(115)).Circle("S-GRID", S(-2400), S(115), S(400)).Text("S-GRID", S(-2400), S(115), S(300), "A");
            d.Line("S-GRID", S(-2000), S(7885), S(12000), S(7885)).Circle("S-GRID", S(-2400), S(7885), S(400)).Text("S-GRID", S(-2400), S(7885), S(300), "B");

            // rooms
            d.Text("A-AREA-IDEN", S(2000), S(6000), S(200), "BEDROOM");
            d.Text("A-AREA-IDEN", S(2000), S(5600), S(150), "4700 X 3650");
            d.Text("A-AREA-IDEN", S(1200), S(3000), S(200), "KITCHEN");

            // dimension across wall B (230 between faces)
            d.Dim("A-ANNO-DIMS", S(1000), S(3885), S(1000), S(4115), 90);

            // plan title
            d.Text("A-ANNO-TTLB", S(3000), S(-3500), S(400), "SECOND FLOOR PLAN");
            return d.Build();
        }

        public static RevitSnapshot BuildSnapshot()
        {
            var t = CadToRevit;
            var snap = new RevitSnapshot { ProjectName = "Test Tower", DocumentPath = "test.rvt" };
            snap.Levels.Add(new QcLevel { Id = "1", Name = "Ground Floor", ElevationMm = 0 });
            snap.Levels.Add(new QcLevel { Id = "2", Name = "First Floor", ElevationMm = 3000 });
            snap.Levels.Add(new QcLevel { Id = "3", Name = "Second Floor", ElevationMm = 6000 });
            const string L = "Second Floor";
            int id = 1000;
            void Wall(double x1, double y1, double x2, double y2, double th, string type)
            {
                snap.Walls.Add(new QcWall
                {
                    Id = (id++).ToString(), Centerline = t.Apply(new Seg2(new Vec2(x1, y1), new Vec2(x2, y2))), Thickness = th,
                    TypeName = type, Levels = new List<string> { L }, BaseLevel = L
                });
            }
            // exterior (centre lines meet at the corners, as Revit joins do)
            Wall(115, 115, 9885, 115, 230, "Brick 230");
            Wall(9885, 115, 9885, 7885, 230, "Brick 230");
            Wall(9885, 7885, 115, 7885, 230, "Brick 230");
            Wall(115, 7885, 115, 115, 230, "Brick 230");
            Wall(5000, 115, 5000, 7885, 115, "Brick 115");       // A
            Wall(115, 4000, 5000, 4000, 200, "Brick 230");       // B: modelled 200 (and type name says 230)
            Wall(7500, 115, 7500, 3000, 115, "Brick 115");       // extra
            var wallA = snap.Walls[4].Id;

            snap.Openings.Add(new QcOpening { Id = "2001", Kind = OpeningKind.Door, Position = t.Apply(new Vec2(5000, 1450)), Width = 1000, LevelName = L, TypeName = "Single 1000", TypeMark = "D1", HostWallId = wallA });

            snap.Columns.Add(new QcColumn { Id = "3001", Center = t.Apply(new Vec2(2500, 2000)), Width = 300, Depth = 450, Angle = GeoMath.NormalizeUndirected(t.Rotation), LevelName = L, TypeName = "300x450" });
            snap.Columns.Add(new QcColumn { Id = "3002", Center = t.Apply(new Vec2(7560, 2000)), Width = 300, Depth = 300, Angle = GeoMath.NormalizeUndirected(t.Rotation), LevelName = L, TypeName = "300x300" });

            snap.Grids.Add(new QcGrid { Id = "4001", Name = "1", Line = t.Apply(new Seg2(new Vec2(115, -3000), new Vec2(115, 11000))) });
            snap.Grids.Add(new QcGrid { Id = "4002", Name = "2", Line = t.Apply(new Seg2(new Vec2(9885, -3000), new Vec2(9885, 11000))) });
            snap.Grids.Add(new QcGrid { Id = "4003", Name = "A", Line = t.Apply(new Seg2(new Vec2(-3000, 115), new Vec2(13000, 115))) });
            snap.Grids.Add(new QcGrid { Id = "4004", Name = "B", Line = t.Apply(new Seg2(new Vec2(-3000, 7885), new Vec2(13000, 7885))) });

            List<Vec2> Poly(params double[] xy)
            {
                var l = new List<Vec2>();
                for (int i = 0; i < xy.Length; i += 2) l.Add(t.Apply(new Vec2(xy[i], xy[i + 1])));
                return l;
            }
            snap.Rooms.Add(new QcRoom { Id = "5001", Name = "Bedroom", Number = "201", LevelName = L, Position = t.Apply(new Vec2(2500, 6000)),
                Boundary = Poly(230, 4100, 4942.5, 4100, 4942.5, 7770, 230, 7770), AreaM2 = 17.2 });
            snap.Rooms.Add(new QcRoom { Id = "5002", Name = "Dining", Number = "202", LevelName = L, Position = t.Apply(new Vec2(2500, 2000)),
                Boundary = Poly(230, 230, 4942.5, 230, 4942.5, 3900, 230, 3900), AreaM2 = 17.3 });
            return snap;
        }
    }
}
