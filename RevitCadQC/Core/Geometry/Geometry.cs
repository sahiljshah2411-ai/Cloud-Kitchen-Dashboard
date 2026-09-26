using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace RevitCadQC.Core.Geometry
{
    /// <summary>2D vector / point. All QC geometry is in millimetres.</summary>
    public struct Vec2 : IEquatable<Vec2>
    {
        public double X;
        public double Y;

        public Vec2(double x, double y) { X = x; Y = y; }

        public static readonly Vec2 Zero = new Vec2(0, 0);

        [JsonIgnore]
        public double Length => Math.Sqrt(X * X + Y * Y);
        [JsonIgnore]
        public double LengthSq => X * X + Y * Y;

        public Vec2 Normalized()
        {
            double l = Length;
            return l < 1e-12 ? Zero : new Vec2(X / l, Y / l);
        }

        /// <summary>Left-hand perpendicular (rotated +90°).</summary>
        [JsonIgnore]
        public Vec2 Perp => new Vec2(-Y, X);

        public double Dot(Vec2 o) => X * o.X + Y * o.Y;
        public double Cross(Vec2 o) => X * o.Y - Y * o.X;
        public double DistanceTo(Vec2 o) => (this - o).Length;

        public Vec2 Rotate(double radians)
        {
            double c = Math.Cos(radians), s = Math.Sin(radians);
            return new Vec2(X * c - Y * s, X * s + Y * c);
        }

        public static Vec2 FromAngle(double radians) => new Vec2(Math.Cos(radians), Math.Sin(radians));

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator -(Vec2 a) => new Vec2(-a.X, -a.Y);
        public static Vec2 operator *(Vec2 a, double s) => new Vec2(a.X * s, a.Y * s);
        public static Vec2 operator *(double s, Vec2 a) => new Vec2(a.X * s, a.Y * s);
        public static Vec2 operator /(Vec2 a, double s) => new Vec2(a.X / s, a.Y / s);

        public bool Equals(Vec2 o) => X == o.X && Y == o.Y;
        public override bool Equals(object obj) => obj is Vec2 v && Equals(v);
        public override int GetHashCode() => X.GetHashCode() * 397 ^ Y.GetHashCode();
        public override string ToString() => $"({X:0.#}, {Y:0.#})";
    }

    /// <summary>Straight 2D segment.</summary>
    public sealed class Seg2
    {
        public Vec2 A;
        public Vec2 B;

        public Seg2(Vec2 a, Vec2 b) { A = a; B = b; }

        [JsonIgnore]
        public double Length => (B - A).Length;
        [JsonIgnore]
        public Vec2 Dir => (B - A).Normalized();
        [JsonIgnore]
        public Vec2 Mid => (A + B) * 0.5;

        /// <summary>Undirected angle in [0, π).</summary>
        [JsonIgnore]
        public double Angle => GeoMath.NormalizeUndirected(Math.Atan2(B.Y - A.Y, B.X - A.X));

        /// <summary>Parameter of the projection of p onto the infinite line, in length units from A.</summary>
        public double Project(Vec2 p) => (p - A).Dot(Dir);

        public Vec2 PointAt(double t) => A + Dir * t;

        /// <summary>Signed perpendicular distance of p from the infinite line (positive = left side).</summary>
        public double SignedOffset(Vec2 p) => Dir.Cross(p - A);

        public double DistanceToPoint(Vec2 p)
        {
            double len = Length;
            if (len < 1e-9) return p.DistanceTo(A);
            double t = Math.Max(0, Math.Min(len, Project(p)));
            return p.DistanceTo(PointAt(t));
        }

        public Seg2 Reversed() => new Seg2(B, A);
        [JsonIgnore]
        public Box2 Bounds => Box2.FromPoints(new[] { A, B });
        public override string ToString() => $"{A}->{B}";
    }

    public struct Box2
    {
        public double MinX, MinY, MaxX, MaxY;

        public Box2(double minX, double minY, double maxX, double maxY)
        {
            MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
        }

        public static Box2 Empty => new Box2(double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
        [JsonIgnore]
        public bool IsEmpty => MinX > MaxX || MinY > MaxY;
        [JsonIgnore]
        public double Width => MaxX - MinX;
        [JsonIgnore]
        public double Height => MaxY - MinY;
        [JsonIgnore]
        public Vec2 Center => new Vec2((MinX + MaxX) / 2, (MinY + MaxY) / 2);

        public static Box2 FromPoints(IEnumerable<Vec2> pts)
        {
            var b = Empty;
            foreach (var p in pts) b = b.Include(p);
            return b;
        }

        public Box2 Include(Vec2 p) => new Box2(Math.Min(MinX, p.X), Math.Min(MinY, p.Y), Math.Max(MaxX, p.X), Math.Max(MaxY, p.Y));

        public Box2 Union(Box2 o)
        {
            if (IsEmpty) return o;
            if (o.IsEmpty) return this;
            return new Box2(Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY), Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY));
        }

        public Box2 Inflate(double d) => new Box2(MinX - d, MinY - d, MaxX + d, MaxY + d);
        public bool Contains(Vec2 p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;
        public bool Intersects(Box2 o) => !(o.MinX > MaxX || o.MaxX < MinX || o.MinY > MaxY || o.MaxY < MinY);

        public double DistanceTo(Vec2 p)
        {
            double dx = Math.Max(0, Math.Max(MinX - p.X, p.X - MaxX));
            double dy = Math.Max(0, Math.Max(MinY - p.Y, p.Y - MaxY));
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    /// <summary>General 2D affine transform: [a c e; b d f]. Used for block inserts (non-uniform scale / mirror).</summary>
    public struct Affine2
    {
        public double A, B, C, D, E, F;

        public Affine2(double a, double b, double c, double d, double e, double f)
        {
            A = a; B = b; C = c; D = d; E = e; F = f;
        }

        public static Affine2 Identity => new Affine2(1, 0, 0, 1, 0, 0);
        public static Affine2 Translation(double x, double y) => new Affine2(1, 0, 0, 1, x, y);
        public static Affine2 Scale(double sx, double sy) => new Affine2(sx, 0, 0, sy, 0, 0);

        public static Affine2 Rotation(double r)
        {
            double c = Math.Cos(r), s = Math.Sin(r);
            return new Affine2(c, s, -s, c, 0, 0);
        }

        public Vec2 Apply(Vec2 p) => new Vec2(A * p.X + C * p.Y + E, B * p.X + D * p.Y + F);
        public Vec2 ApplyVector(Vec2 v) => new Vec2(A * v.X + C * v.Y, B * v.X + D * v.Y);

        /// <summary>Returns this ∘ other (other applied first).</summary>
        public Affine2 Compose(Affine2 o) => new Affine2(
            A * o.A + C * o.B,
            B * o.A + D * o.B,
            A * o.C + C * o.D,
            B * o.C + D * o.D,
            A * o.E + C * o.F + E,
            B * o.E + D * o.F + F);

        [JsonIgnore]
        public double Determinant => A * D - B * C;
        [JsonIgnore]
        public bool IsMirrored => Determinant < 0;

        /// <summary>Approximate uniform scale factor (geometric mean of axis scales).</summary>
        [JsonIgnore]
        public double UniformScale => Math.Sqrt(Math.Abs(Determinant));

        /// <summary>Rotation angle of the transformed X axis.</summary>
        [JsonIgnore]
        public double RotationAngle => Math.Atan2(B, A);
    }

    /// <summary>Rigid-with-scale transform used to align CAD to Revit: p' = s·R(θ)·p + t.</summary>
    public sealed class Similarity2
    {
        public double Rotation { get; set; }
        public double Scale { get; set; } = 1.0;
        public double Tx { get; set; }
        public double Ty { get; set; }

        public static Similarity2 Identity => new Similarity2();

        public Vec2 Apply(Vec2 p)
        {
            var r = p.Rotate(Rotation) * Scale;
            return new Vec2(r.X + Tx, r.Y + Ty);
        }

        public Vec2 ApplyVector(Vec2 v) => v.Rotate(Rotation) * Scale;

        public Seg2 Apply(Seg2 s) => new Seg2(Apply(s.A), Apply(s.B));

        public Similarity2 Inverse()
        {
            double inv = 1.0 / Scale;
            var t = new Vec2(-Tx, -Ty).Rotate(-Rotation) * inv;
            return new Similarity2 { Rotation = -Rotation, Scale = inv, Tx = t.X, Ty = t.Y };
        }

        /// <summary>Returns this ∘ first (first applied first).</summary>
        public Similarity2 After(Similarity2 first)
        {
            var t = Apply(new Vec2(first.Tx, first.Ty));
            return new Similarity2
            {
                Rotation = GeoMath.NormalizeSigned(Rotation + first.Rotation),
                Scale = Scale * first.Scale,
                Tx = t.X,
                Ty = t.Y
            };
        }

        /// <summary>Least-squares rigid (optionally scaled) fit mapping src[i] to dst[i] (Umeyama / Procrustes in 2D).</summary>
        public static Similarity2 Fit(IList<Vec2> src, IList<Vec2> dst, bool allowScale = false)
        {
            int n = Math.Min(src.Count, dst.Count);
            if (n == 0) return Identity;
            var cs = Vec2.Zero; var cd = Vec2.Zero;
            for (int i = 0; i < n; i++) { cs += src[i]; cd += dst[i]; }
            cs /= n; cd /= n;
            if (n == 1) return new Similarity2 { Tx = cd.X - cs.X, Ty = cd.Y - cs.Y };

            double sxx = 0, sxy = 0, varS = 0;
            for (int i = 0; i < n; i++)
            {
                var a = src[i] - cs;
                var b = dst[i] - cd;
                sxx += a.X * b.X + a.Y * b.Y;
                sxy += a.X * b.Y - a.Y * b.X;
                varS += a.LengthSq;
            }
            double rot = Math.Atan2(sxy, sxx);
            double scale = 1.0;
            if (allowScale && varS > 1e-9) scale = Math.Sqrt(sxx * sxx + sxy * sxy) / varS;
            var rc = cs.Rotate(rot) * scale;
            return new Similarity2 { Rotation = rot, Scale = scale, Tx = cd.X - rc.X, Ty = cd.Y - rc.Y };
        }

        public override string ToString() =>
            $"rot={Rotation * 180 / Math.PI:0.###}° scale={Scale:0.#####} t=({Tx:0.#}, {Ty:0.#})";
    }

    public static class GeoMath
    {
        public const double Deg = Math.PI / 180.0;

        public static double NormalizeUndirected(double a)
        {
            a %= Math.PI;
            if (a < 0) a += Math.PI;
            if (a >= Math.PI - 1e-12) a = 0;
            return a;
        }

        public static double NormalizeSigned(double a)
        {
            while (a > Math.PI) a -= 2 * Math.PI;
            while (a <= -Math.PI) a += 2 * Math.PI;
            return a;
        }

        /// <summary>Smallest difference between two undirected angles, in [0, π/2].</summary>
        public static double UndirectedDiff(double a, double b)
        {
            double d = Math.Abs(NormalizeUndirected(a) - NormalizeUndirected(b));
            return Math.Min(d, Math.PI - d);
        }

        public static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        /// <summary>Tessellates an arc (angles in radians, CCW from start to end).</summary>
        public static List<Vec2> TessellateArc(Vec2 c, double r, double start, double end, double maxChordAngle = 10 * Deg)
        {
            double sweep = end - start;
            while (sweep <= 0) sweep += 2 * Math.PI;
            int n = Math.Max(1, (int)Math.Ceiling(sweep / maxChordAngle));
            var pts = new List<Vec2>(n + 1);
            for (int i = 0; i <= n; i++)
            {
                double a = start + sweep * i / n;
                pts.Add(c + Vec2.FromAngle(a) * r);
            }
            return pts;
        }

        /// <summary>Points for a polyline bulge arc between p1 and p2 (exclusive of p1, inclusive of p2).</summary>
        public static List<Vec2> BulgePoints(Vec2 p1, Vec2 p2, double bulge)
        {
            var res = new List<Vec2>();
            if (Math.Abs(bulge) < 1e-9) { res.Add(p2); return res; }
            double chord = p1.DistanceTo(p2);
            if (chord < 1e-9) { res.Add(p2); return res; }
            double theta = 4 * Math.Atan(bulge);
            double r = chord / (2 * Math.Sin(Math.Abs(theta) / 2));
            var mid = (p1 + p2) * 0.5;
            var dir = (p2 - p1).Normalized();
            double h = Math.Sqrt(Math.Max(0, r * r - chord * chord / 4));
            // centre is on the left of the chord for positive bulge when |theta| < π
            double sign = (bulge > 0) == (Math.Abs(theta) < Math.PI) ? 1 : -1;
            var c = mid + dir.Perp * (h * sign);
            double a1 = Math.Atan2(p1.Y - c.Y, p1.X - c.X);
            int n = Math.Max(2, (int)Math.Ceiling(Math.Abs(theta) / (10 * Deg)));
            for (int i = 1; i <= n; i++)
            {
                double a = a1 + theta * i / n;
                res.Add(c + Vec2.FromAngle(a) * r);
            }
            res[res.Count - 1] = p2;
            return res;
        }

        public static double PolygonArea(IList<Vec2> poly)
        {
            double a = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var p = poly[i];
                var q = poly[(i + 1) % poly.Count];
                a += p.X * q.Y - q.X * p.Y;
            }
            return a / 2;
        }

        public static bool PointInPolygon(Vec2 p, IList<Vec2> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var a = poly[i];
                var b = poly[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) &&
                    p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y + 1e-300) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>Minimum-area oriented bounding rectangle (rotating calipers over hull edges).</summary>
        public static OrientedRect MinAreaRect(IList<Vec2> pts)
        {
            var hull = ConvexHull(pts);
            if (hull.Count == 0) return null;
            if (hull.Count < 3)
            {
                var b = Box2.FromPoints(hull);
                return new OrientedRect { Center = b.Center, Width = b.Width, Depth = b.Height, Angle = 0 };
            }
            OrientedRect best = null;
            for (int i = 0; i < hull.Count; i++)
            {
                var e = hull[(i + 1) % hull.Count] - hull[i];
                if (e.Length < 1e-9) continue;
                double ang = Math.Atan2(e.Y, e.X);
                double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
                var u = Vec2.FromAngle(ang);
                var v = u.Perp;
                foreach (var p in hull)
                {
                    double pu = p.Dot(u), pv = p.Dot(v);
                    minU = Math.Min(minU, pu); maxU = Math.Max(maxU, pu);
                    minV = Math.Min(minV, pv); maxV = Math.Max(maxV, pv);
                }
                double area = (maxU - minU) * (maxV - minV);
                if (best == null || area < best.Width * best.Depth - 1e-6)
                {
                    var c = u * ((minU + maxU) / 2) + v * ((minV + maxV) / 2);
                    best = new OrientedRect { Center = c, Width = maxU - minU, Depth = maxV - minV, Angle = NormalizeUndirected(ang) };
                }
            }
            return best;
        }

        public static List<Vec2> ConvexHull(IList<Vec2> input)
        {
            var pts = input.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
            if (pts.Count < 3) return pts;
            var hull = new List<Vec2>();
            for (int pass = 0; pass < 2; pass++)
            {
                int start = hull.Count;
                foreach (var p in pts)
                {
                    while (hull.Count >= start + 2 && (hull[hull.Count - 1] - hull[hull.Count - 2]).Cross(p - hull[hull.Count - 2]) <= 0)
                        hull.RemoveAt(hull.Count - 1);
                    hull.Add(p);
                }
                hull.RemoveAt(hull.Count - 1);
                pts.Reverse();
            }
            return hull;
        }

        /// <summary>Merges intervals and returns total covered length within [lo, hi].</summary>
        public static List<Interval> MergeIntervals(IEnumerable<Interval> items)
        {
            var list = items.Where(i => i.Hi > i.Lo).OrderBy(i => i.Lo).ToList();
            var res = new List<Interval>();
            foreach (var it in list)
            {
                if (res.Count > 0 && it.Lo <= res[res.Count - 1].Hi + 1e-6)
                {
                    var last = res[res.Count - 1];
                    res[res.Count - 1] = new Interval(last.Lo, Math.Max(last.Hi, it.Hi));
                }
                else res.Add(it);
            }
            return res;
        }

        /// <summary>Parts of [lo,hi] not covered by the given intervals.</summary>
        public static List<Interval> Uncovered(double lo, double hi, IEnumerable<Interval> covered)
        {
            var merged = MergeIntervals(covered.Select(c => new Interval(Math.Max(lo, c.Lo), Math.Min(hi, c.Hi))));
            var res = new List<Interval>();
            double cur = lo;
            foreach (var m in merged)
            {
                if (m.Lo > cur) res.Add(new Interval(cur, m.Lo));
                cur = Math.Max(cur, m.Hi);
            }
            if (cur < hi) res.Add(new Interval(cur, hi));
            return res;
        }
    }

    public struct Interval
    {
        public double Lo;
        public double Hi;
        public Interval(double lo, double hi) { Lo = lo; Hi = hi; }
        [JsonIgnore]
        public double Length => Hi - Lo;
        public override string ToString() => $"[{Lo:0.#}, {Hi:0.#}]";
    }

    public sealed class OrientedRect
    {
        public Vec2 Center { get; set; }
        /// <summary>Extent along <see cref="Angle"/>.</summary>
        public double Width { get; set; }
        /// <summary>Extent perpendicular to <see cref="Angle"/>.</summary>
        public double Depth { get; set; }
        /// <summary>Undirected angle in radians.</summary>
        public double Angle { get; set; }

        [JsonIgnore]
        public double Min => Math.Min(Width, Depth);
        [JsonIgnore]
        public double Max => Math.Max(Width, Depth);

        public List<Vec2> Corners()
        {
            var u = Vec2.FromAngle(Angle) * (Width / 2);
            var v = Vec2.FromAngle(Angle).Perp * (Depth / 2);
            return new List<Vec2> { Center - u - v, Center + u - v, Center + u + v, Center - u + v };
        }
    }

    /// <summary>Uniform-grid spatial hash for fast neighbour queries on points.</summary>
    public sealed class SpatialHash<T>
    {
        private readonly double _cell;
        private readonly Dictionary<long, List<(Vec2 p, T item)>> _cells = new Dictionary<long, List<(Vec2, T)>>();

        public SpatialHash(double cellSize) { _cell = Math.Max(1e-6, cellSize); }

        private long Key(int ix, int iy) => ((long)ix << 32) ^ (uint)iy;

        public void Add(Vec2 p, T item)
        {
            int ix = (int)Math.Floor(p.X / _cell), iy = (int)Math.Floor(p.Y / _cell);
            long k = Key(ix, iy);
            if (!_cells.TryGetValue(k, out var l)) _cells[k] = l = new List<(Vec2, T)>();
            l.Add((p, item));
        }

        public IEnumerable<(Vec2 p, T item)> Query(Vec2 p, double radius)
        {
            int x0 = (int)Math.Floor((p.X - radius) / _cell), x1 = (int)Math.Floor((p.X + radius) / _cell);
            int y0 = (int)Math.Floor((p.Y - radius) / _cell), y1 = (int)Math.Floor((p.Y + radius) / _cell);
            double r2 = radius * radius;
            for (int ix = x0; ix <= x1; ix++)
                for (int iy = y0; iy <= y1; iy++)
                    if (_cells.TryGetValue(Key(ix, iy), out var l))
                        foreach (var e in l)
                            if ((e.p - p).LengthSq <= r2) yield return e;
        }

        public bool TryNearest(Vec2 p, double radius, out Vec2 best, out T bestItem)
        {
            best = default; bestItem = default;
            double bd = double.MaxValue;
            bool found = false;
            foreach (var e in Query(p, radius))
            {
                double d = (e.p - p).LengthSq;
                if (d < bd) { bd = d; best = e.p; bestItem = e.item; found = true; }
            }
            return found;
        }
    }
}
