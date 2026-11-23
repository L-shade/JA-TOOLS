using System;
using System.Collections.Generic;

namespace JA.IfcViewer.Geometry
{
    public struct Vec3
    {
        public double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);
        public static readonly Vec3 UnitX = new Vec3(1, 0, 0), UnitY = new Vec3(0, 1, 0), UnitZ = new Vec3(0, 0, 1);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(double s, Vec3 a) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator /(Vec3 a, double s) => new Vec3(a.X / s, a.Y / s, a.Z / s);

        public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public Vec3 Cross(Vec3 b) => new Vec3(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public double LengthSq => X * X + Y * Y + Z * Z;
        public Vec3 Normalized() { double l = Length; return l > 1e-300 ? this / l : this; }
        public static Vec3 Lerp(Vec3 a, Vec3 b, double t) => a + (b - a) * t;
        public double DistanceTo(Vec3 b) => (this - b).Length;
        public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";

        /// <summary>Any unit vector perpendicular to this one.</summary>
        public Vec3 AnyPerpendicular()
        {
            Vec3 a = Math.Abs(X) < 0.9 ? UnitX : UnitY;
            return Cross(a).Normalized();
        }
    }

    public struct Vec2
    {
        public double X, Y;
        public Vec2(double x, double y) { X = x; Y = y; }
        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator *(Vec2 a, double s) => new Vec2(a.X * s, a.Y * s);
        public double Cross(Vec2 b) => X * b.Y - Y * b.X;
        public double Dot(Vec2 b) => X * b.X + Y * b.Y;
        public double Length => Math.Sqrt(X * X + Y * Y);
        public override string ToString() => $"({X:0.###}, {Y:0.###})";
    }

    /// <summary>Affine transform: columns X, Y, Z (may carry scale) and translation T. p' = X*p.x + Y*p.y + Z*p.z + T.</summary>
    public struct Mat
    {
        public Vec3 X, Y, Z, T;

        public static readonly Mat Identity = new Mat { X = Vec3.UnitX, Y = Vec3.UnitY, Z = Vec3.UnitZ, T = Vec3.Zero };

        public Mat(Vec3 x, Vec3 y, Vec3 z, Vec3 t) { X = x; Y = y; Z = z; T = t; }

        public Vec3 Point(Vec3 p) => new Vec3(
            X.X * p.X + Y.X * p.Y + Z.X * p.Z + T.X,
            X.Y * p.X + Y.Y * p.Y + Z.Y * p.Z + T.Y,
            X.Z * p.X + Y.Z * p.Y + Z.Z * p.Z + T.Z);

        public Vec3 Vector(Vec3 v) => new Vec3(
            X.X * v.X + Y.X * v.Y + Z.X * v.Z,
            X.Y * v.X + Y.Y * v.Y + Z.Y * v.Z,
            X.Z * v.X + Y.Z * v.Y + Z.Z * v.Z);

        /// <summary>a * b: apply b first, then a.</summary>
        public static Mat operator *(Mat a, Mat b) => new Mat(a.Vector(b.X), a.Vector(b.Y), a.Vector(b.Z), a.Point(b.T));

        public double Determinant => X.Dot(Y.Cross(Z));

        public bool IsIdentity =>
            (X - Vec3.UnitX).LengthSq < 1e-18 && (Y - Vec3.UnitY).LengthSq < 1e-18 && (Z - Vec3.UnitZ).LengthSq < 1e-18 && T.LengthSq < 1e-18;

        public static Mat Translation(Vec3 t) => new Mat(Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ, t);

        /// <summary>Rotation about an axis through the origin (Rodrigues).</summary>
        public static Mat Rotation(Vec3 axis, double ang)
        {
            Vec3 k = axis.Normalized();
            double c = Math.Cos(ang), s = Math.Sin(ang), t = 1 - c;
            Vec3 Col(Vec3 v) => v * c + k.Cross(v) * s + k * (k.Dot(v) * t);
            return new Mat(Col(Vec3.UnitX), Col(Vec3.UnitY), Col(Vec3.UnitZ), Vec3.Zero);
        }

        /// <summary>Frame from an origin, a Z axis and an approximate X axis (orthonormalised; Y = Z × X).</summary>
        public static Mat Frame(Vec3 origin, Vec3 z, Vec3 xApprox)
        {
            Vec3 zz = z.LengthSq > 1e-24 ? z.Normalized() : Vec3.UnitZ;
            Vec3 xx = xApprox - zz * xApprox.Dot(zz);
            if (xx.LengthSq < 1e-16)
            {
                Vec3 alt = Math.Abs(zz.X) < 0.9 ? Vec3.UnitX : Vec3.UnitY;
                xx = alt - zz * alt.Dot(zz);
            }
            xx = xx.Normalized();
            Vec3 yy = zz.Cross(xx);
            return new Mat(xx, yy, zz, origin);
        }
    }

    public struct Bounds
    {
        public Vec3 Min, Max;
        public static Bounds Empty => new Bounds { Min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue), Max = new Vec3(double.MinValue, double.MinValue, double.MinValue) };
        public bool IsEmpty => Min.X > Max.X;
        public void Add(Vec3 p)
        {
            if (p.X < Min.X) Min.X = p.X; if (p.Y < Min.Y) Min.Y = p.Y; if (p.Z < Min.Z) Min.Z = p.Z;
            if (p.X > Max.X) Max.X = p.X; if (p.Y > Max.Y) Max.Y = p.Y; if (p.Z > Max.Z) Max.Z = p.Z;
        }
        public void Add(Bounds b) { if (b.IsEmpty) return; Add(b.Min); Add(b.Max); }
        public Vec3 Center => (Min + Max) * 0.5;
        public Vec3 Size => IsEmpty ? Vec3.Zero : Max - Min;
        public double Diagonal => IsEmpty ? 0 : (Max - Min).Length;
        public bool Intersects(Bounds o, double tol = 0) =>
            !IsEmpty && !o.IsEmpty &&
            Min.X <= o.Max.X + tol && Max.X >= o.Min.X - tol &&
            Min.Y <= o.Max.Y + tol && Max.Y >= o.Min.Y - tol &&
            Min.Z <= o.Max.Z + tol && Max.Z >= o.Min.Z - tol;

        public static Bounds Of(IList<Vec3> pts)
        {
            Bounds b = Empty;
            for (int i = 0; i < pts.Count; i++) b.Add(pts[i]);
            return b;
        }
    }

    /// <summary>A triangle soup (3 consecutive points per triangle), in double precision.</summary>
    public sealed class Mesh
    {
        public readonly List<Vec3> P;
        public Mesh() { P = new List<Vec3>(); }
        public Mesh(int capacity) { P = new List<Vec3>(capacity); }
        public Mesh(List<Vec3> p) { P = p; }
        public int TriangleCount => P.Count / 3;
        public bool IsEmpty => P.Count < 3;

        public void Add(Vec3 a, Vec3 b, Vec3 c)
        {
            // drop degenerate triangles
            if ((b - a).Cross(c - a).LengthSq < 1e-24) return;
            P.Add(a); P.Add(b); P.Add(c);
        }

        public void AddAll(Mesh m) { if (m != null) P.AddRange(m.P); }

        public Mesh Transformed(Mat m)
        {
            var r = new Mesh(P.Count);
            bool flip = m.Determinant < 0;
            for (int i = 0; i + 2 < P.Count; i += 3)
            {
                Vec3 a = m.Point(P[i]), b = m.Point(P[i + 1]), c = m.Point(P[i + 2]);
                if (flip) { r.P.Add(a); r.P.Add(c); r.P.Add(b); }
                else { r.P.Add(a); r.P.Add(b); r.P.Add(c); }
            }
            return r;
        }

        public void Flip()
        {
            for (int i = 0; i + 2 < P.Count; i += 3) { Vec3 t = P[i + 1]; P[i + 1] = P[i + 2]; P[i + 2] = t; }
        }

        public Bounds Bounds => Bounds.Of(P);

        /// <summary>Signed volume (positive when the triangles face outwards of a closed solid).</summary>
        public double SignedVolume()
        {
            double v = 0;
            Vec3 o = P.Count > 0 ? P[0] : Vec3.Zero;
            for (int i = 0; i + 2 < P.Count; i += 3)
            {
                Vec3 a = P[i] - o, b = P[i + 1] - o, c = P[i + 2] - o;
                v += a.Dot(b.Cross(c));
            }
            return v / 6.0;
        }

        /// <summary>Turns a closed mesh outwards when it was described inside out.</summary>
        public void Orient()
        {
            if (SignedVolume() < 0) Flip();
        }
    }
}
