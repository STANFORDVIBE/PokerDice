// Векторы и матрицы 3x3 (y вверх, z на зрителя), честный рандом — всё как в старой версии на three.js
using System.Security.Cryptography;

namespace PokerDice;

readonly struct V3
{
    public readonly double X, Y, Z;
    public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static V3 operator -(V3 a) => new(-a.X, -a.Y, -a.Z);
    public static V3 operator *(V3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    public static V3 operator *(V3 a, V3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
    public static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static V3 Cross(V3 a, V3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public V3 Norm() { var l = Length; return new(X / l, Y / l, Z / l); }
    public static V3 Lerp(V3 a, V3 b, double t) => a + (b - a) * t;
    public double Max => Math.Max(X, Math.Max(Y, Z));
}

readonly struct M3
{
    public readonly V3 R0, R1, R2;   // строки
    public M3(V3 r0, V3 r1, V3 r2) { R0 = r0; R1 = r1; R2 = r2; }

    public static readonly M3 I = new(new(1, 0, 0), new(0, 1, 0), new(0, 0, 1));
    public static M3 RotX(double a) { double c = Math.Cos(a), s = Math.Sin(a); return new(new(1, 0, 0), new(0, c, -s), new(0, s, c)); }
    public static M3 RotY(double a) { double c = Math.Cos(a), s = Math.Sin(a); return new(new(c, 0, s), new(0, 1, 0), new(-s, 0, c)); }
    public static M3 RotAxis(V3 v, double a)
    {
        double c = Math.Cos(a), s = Math.Sin(a), t = 1 - c, x = v.X, y = v.Y, z = v.Z;
        return new(new(t * x * x + c, t * x * y - s * z, t * x * z + s * y),
                   new(t * x * y + s * z, t * y * y + c, t * y * z - s * x),
                   new(t * x * z - s * y, t * y * z + s * x, t * z * z + c));
    }

    public M3 T => new(new(R0.X, R1.X, R2.X), new(R0.Y, R1.Y, R2.Y), new(R0.Z, R1.Z, R2.Z));
    public static V3 operator *(M3 m, V3 v) => new(V3.Dot(m.R0, v), V3.Dot(m.R1, v), V3.Dot(m.R2, v));
    public static M3 operator *(M3 a, M3 b) { var bt = b.T; return new(bt * a.R0, bt * a.R1, bt * a.R2); }

    // ось и угол поворота через кватернион — устойчиво у 0° и 180°
    public (V3 axis, double angle) AxisAngle()
    {
        double m00 = R0.X, m01 = R0.Y, m02 = R0.Z, m10 = R1.X, m11 = R1.Y, m12 = R1.Z, m20 = R2.X, m21 = R2.Y, m22 = R2.Z;
        double tr = m00 + m11 + m22, w, x, y, z, s;
        if (tr > 0) { s = Math.Sqrt(tr + 1) * 2; w = s / 4; x = (m21 - m12) / s; y = (m02 - m20) / s; z = (m10 - m01) / s; }
        else if (m00 > m11 && m00 > m22) { s = Math.Sqrt(1 + m00 - m11 - m22) * 2; w = (m21 - m12) / s; x = s / 4; y = (m01 + m10) / s; z = (m02 + m20) / s; }
        else if (m11 > m22) { s = Math.Sqrt(1 + m11 - m00 - m22) * 2; w = (m02 - m20) / s; x = (m01 + m10) / s; y = s / 4; z = (m12 + m21) / s; }
        else { s = Math.Sqrt(1 + m22 - m00 - m11) * 2; w = (m10 - m01) / s; x = (m02 + m20) / s; y = (m12 + m21) / s; z = s / 4; }
        double n = Math.Sqrt(x * x + y * y + z * z);
        if (n < 1e-9) return (new(0, 1, 0), 0);
        return (new(x / n, y / n, z / n), 2 * Math.Atan2(n, w));
    }
}

static class Rng
{
    // честный случайный 0..n-1 без перекоса (криптографический генератор)
    public static int Next(int n) => RandomNumberGenerator.GetInt32(n);
    public static int Roll100() => Next(100) + 1;
}
