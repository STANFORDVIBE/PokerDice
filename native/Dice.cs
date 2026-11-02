// Виды кубиков. d4, d8, d10, d12, d20 — выпуклые многогранники, заданные плоскостями граней (N·p = D),
// рёбра скруглены мягким максимумом плоскостей. d6 и d100 — куб со скруглёнными рёбрами, его рисует свой точный код.
namespace PokerDice;

sealed class DieKind
{
    public readonly string Id;
    public readonly int Sides;                      // числа на кубике: 1..Sides
    public readonly bool Cube;
    public bool Random => Sides > Faces;            // d100: на шести гранях случайные числа, выпавшее — на лицевой
    public readonly V3[] N, U, V, C;                // по граням: нормаль, надпись вправо и вверх, центр надписи
    public readonly double[] D;
    public readonly M3[] ToFront;                   // поворот, выводящий грань лицом к зрителю
    public readonly int[] Label;                    // число на грани
    public readonly V3[] Verts;
    public readonly double W;                       // сторона квадрата надписи (ребро куба — 1): вписанный круг грани
    public readonly double Bound;                   // радиус шара, в который влезает кубик
    public readonly double Floor;                   // где пол под кубиком, лежащим лицевой гранью к зрителю
    public readonly int Tex;                        // размер текстуры надписи
    readonly double sharp;                          // чем больше, тем острее рёбра
    public int Faces => N.Length;
    public string Name => "d" + Sides;

    DieKind(int sides, IEnumerable<V3> normals, double radius, double sharp, M3[] front = null)
    {
        Id = "d" + sides; Sides = sides; this.sharp = sharp; Cube = front != null;
        N = normals.Select(n => n.Norm()).ToArray();
        int f = N.Length;
        D = Enumerable.Repeat(1.0, f).ToArray();

        // вершины — где сходятся три плоскости и ни одна другая не отрезает
        var verts = new List<V3>();
        for (int a = 0; a < f; a++)
            for (int b = a + 1; b < f; b++)
                for (int c = b + 1; c < f; c++)
                {
                    var bc = V3.Cross(N[b], N[c]);
                    double det = V3.Dot(N[a], bc);
                    if (Math.Abs(det) < 1e-9) continue;
                    var p = (bc * D[a] + V3.Cross(N[c], N[a]) * D[b] + V3.Cross(N[a], N[b]) * D[c]) * (1 / det);
                    if (N.Where((n, i) => V3.Dot(n, p) > D[i] + 1e-7).Any()) continue;
                    if (verts.All(v => (v - p).Length > 1e-6)) verts.Add(p);
                }
        double k = radius / verts.Max(v => v.Length);
        for (int i = 0; i < f; i++) D[i] *= k;
        Verts = verts.Select(v => v * k).ToArray();
        Bound = radius + 0.005;

        U = new V3[f]; V = new V3[f]; C = new V3[f]; ToFront = new M3[f];
        double inner = double.MaxValue;
        for (int i = 0; i < f; i++)
        {
            var poly = Verts.Where(v => Math.Abs(V3.Dot(N[i], v) - D[i]) < 1e-6).ToArray();
            var c = poly.Aggregate(default(V3), (s, v) => s + v) * (1.0 / poly.Length);
            if (front != null) { U[i] = front[i].R0; V[i] = front[i].R1; }
            else
            {
                // надпись вверх — к самой дальней вершине: острие треугольника, вершина пятиугольника, нос у d10
                var far = poly.OrderByDescending(v => Math.Round((v - c).Length, 6)).ThenBy(v => Math.Round(v.Y, 6)).ThenBy(v => v.X).First();
                V[i] = (far - c).Norm();
                U[i] = V3.Cross(V[i], N[i]);
            }
            ToFront[i] = new M3(U[i], V[i], N[i]);
            C[i] = c;
            // расстояние от центра до сторон: стороны соединяют соседние по кругу вершины
            var ring = poly.OrderBy(v => Math.Atan2(V3.Dot(v - c, V[i]), V3.Dot(v - c, U[i]))).ToArray();
            for (int j = 0; j < ring.Length; j++)
            {
                var e = (ring[(j + 1) % ring.Length] - ring[j]).Norm();
                var h = c - ring[j];
                inner = Math.Min(inner, (h - e * V3.Dot(h, e)).Length);
            }
        }
        W = 2 * inner;
        Tex = W > 0.7 ? 512 : 256;
        Floor = Verts.Min(v => (ToFront[0] * v).Y);

        // числа: противоположные грани в сумме дают Sides + 1, как на настоящих костях
        Label = new int[f];
        if (Random) return;
        int next = 1;
        for (int i = 0; i < f; i++)
        {
            if (Label[i] != 0) continue;
            Label[i] = next;
            int o = Array.FindIndex(N, n => V3.Dot(n, N[i]) < -0.999);
            if (o >= 0 && Label[o] == 0) Label[o] = sides + 1 - next;
            next++;
        }
    }

    // ---------- геометрия многогранника ----------
    // мягкий максимум расстояний до плоскостей: внутри грани — сама грань, у рёбер и вершин — скругление.
    // Грань под точкой и нормаль: смесь нормалей соседних граней — блик перетекает через ребро без шва
    public (int face, V3 normal) Surface(V3 p)
    {
        double m = double.MinValue; int face = 0;
        Span<double> s = stackalloc double[N.Length];
        for (int i = 0; i < s.Length; i++) { s[i] = V3.Dot(N[i], p) - D[i]; if (s[i] > m) { m = s[i]; face = i; } }
        V3 n = default;
        for (int i = 0; i < s.Length; i++) { double e = (s[i] - m) * sharp; if (e > -12 && i != face) n += N[i] * Math.Exp(e); }
        return (face, (n + N[face]).Norm());
    }

    // для лучей из одной точки (камеры) расстояния до плоскостей считаем раз на кадр
    public double[] Gaps(V3 ro)
    {
        var g = new double[N.Length];
        for (int i = 0; i < g.Length; i++) g[i] = D[i] - V3.Dot(N[i], ro);
        return g;
    }

    // пересечение со скруглённым многогранником: от входа в острый — шагами по расстоянию до поверхности; −1, если мимо.
    // На луче расстояние до плоскости i — t·dn[i] − gap[i], так что шаги обходятся без векторов
    public double Hit(V3 rd, double[] gap)
    {
        int f = N.Length;
        Span<double> dn = stackalloc double[f];
        double tN = double.MinValue, tF = double.MaxValue;
        for (int i = 0; i < f; i++)
        {
            double d = dn[i] = V3.Dot(N[i], rd);
            if (Math.Abs(d) < 1e-12) { if (gap[i] < 0) return -1; continue; }
            double t = gap[i] / d;
            if (d < 0) { if (t > tN) tN = t; }
            else if (t < tF) tF = t;
        }
        if (tN > tF || tF < 0) return -1;
        double at = Math.Max(tN, 0);
        for (int step = 0; step < 48 && at <= tF; step++)
        {
            double m = double.MinValue;
            for (int i = 0; i < f; i++) { double s = at * dn[i] - gap[i]; if (s > m) m = s; }
            double sum = 0;
            for (int i = 0; i < f; i++) { double e = (at * dn[i] - gap[i] - m) * sharp; if (e > -12 && e < 0) sum += Math.Exp(e); }
            double dist = sum == 0 ? m : m + Math.Log(1 + sum) / sharp;
            if (dist < 2e-4) return at;
            at += dist;
        }
        return -1;
    }

    // ---------- набор ----------
    static readonly double PHI = (1 + Math.Sqrt(5)) / 2;

    // все сочетания знаков для (x, y, z), без повторов, если координата нулевая
    static IEnumerable<V3> Signs(double x, double y, double z)
    {
        foreach (var sx in x == 0 ? new[] { 1 } : new[] { 1, -1 })
            foreach (var sy in y == 0 ? new[] { 1 } : new[] { 1, -1 })
                foreach (var sz in z == 0 ? new[] { 1 } : new[] { 1, -1 })
                    yield return new V3(sx * x, sy * y, sz * z);
    }
    static IEnumerable<V3> Cyclic(double x, double y, double z) => Signs(x, y, z).Concat(Signs(z, x, y)).Concat(Signs(y, z, x));

    // повороты, которые выводят грань куба i лицом к зрителю (порядок граней — как был у кубика 1–100)
    static readonly M3[] CUBE_FRONT =
        { M3.RotY(0), M3.RotY(Math.PI), M3.RotY(-Math.PI / 2), M3.RotY(Math.PI / 2), M3.RotX(Math.PI / 2), M3.RotX(-Math.PI / 2) };
    static DieKind CubeOf(int sides) => new(sides, CUBE_FRONT.Select(m => m.R2), Math.Sqrt(3) / 2, 0, CUBE_FRONT);

    // d10 — пятиугольный трапецоэдр: пять граней-«воздушных змеев» сверху и пять снизу, со сдвигом на 36°
    static IEnumerable<V3> D10()
    {
        double b = 39.5 * Math.PI / 180;
        for (int i = 0; i < 10; i++)
        {
            double a = i * Math.PI / 5, up = i % 2 == 0 ? 1 : -1;
            yield return new V3(Math.Cos(b) * Math.Cos(a), up * Math.Sin(b), Math.Cos(b) * Math.Sin(a));
        }
    }

    public static readonly DieKind[] All =
    {
        new(4, new[] { new V3(1, 1, 1), new V3(1, -1, -1), new V3(-1, 1, -1), new V3(-1, -1, 1) }, 0.95, 60),
        CubeOf(6),
        new(8, Signs(1, 1, 1), 0.86, 80),
        new(10, D10(), 0.86, 110),
        new(12, Cyclic(0, 1, PHI), 0.84, 130),
        new(20, Signs(1, 1, 1).Concat(Cyclic(0, 1 / PHI, PHI)), 0.86, 160),
        CubeOf(100),
    };
    public static readonly DieKind Default = All[^1];
}
