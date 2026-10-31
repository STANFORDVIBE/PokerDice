// Кубик рисуется лучами прямо на процессоре, без видеокарты и браузера: форма — куб со скруглёнными рёбрами,
// свет — та же «студия», что была в three.js (софтбокс, отражённый свет от стола, рассеянная подсветка),
// материал — глянцевый лак поверх корпуса, цифры — матовая гравировка. Рисуем только во время анимации.
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace PokerDice;

sealed record DieStyle(string Id, string Name, Color Body, Color Ink, double Grain);

sealed unsafe class DieRenderer
{
    public static readonly DieStyle[] Styles =
    {
        new("classic", "Кость", ColorTranslator.FromHtml("#efeadf"), ColorTranslator.FromHtml("#2a241f"), 1),
        new("poker", "Покер", ColorTranslator.FromHtml("#2c5443"), ColorTranslator.FromHtml("#d9bf86"), 0.35),
        new("casino", "Казино", ColorTranslator.FromHtml("#7a2a2b"), ColorTranslator.FromHtml("#efe6d6"), 0.35),
    };

    public const double SIZE = 104, DIST = 6.7;          // ребро в пикселях при 100%, расстояние до камеры
    const double RAD = 0.06, HALF = 0.5 - RAD;           // скругление рёбер
    // нормали как у суперэллипсоида |x|^24 + |y|^24 + |z|^24 = 1: блик перетекает с грани на ребро без шва
    static double Pow23(double x)
    {
        double x2 = x * x, x4 = x2 * x2, x8 = x4 * x4, x16 = x8 * x8;
        return x16 * x4 * x2 * x;
    }
    const double BOUND = 0.87;                           // радиус шара, в который влезает кубик
    const int TEX = 512, LEVELS = 5;

    public static readonly M3 TILT = M3.RotX(20 * Math.PI / 180);           // смотрим на кубик чуть сверху
    public static readonly M3 VIEW = TILT * M3.RotY(-26 * Math.PI / 180);
    // повороты, которые выводят грань i лицом к зрителю
    public static readonly M3[] TO_FRONT =
        { M3.RotY(0), M3.RotY(Math.PI), M3.RotY(-Math.PI / 2), M3.RotY(Math.PI / 2), M3.RotX(Math.PI / 2), M3.RotX(-Math.PI / 2) };
    static readonly V3[] FU = new V3[6], FV = new V3[6], FN = new V3[6];
    static readonly int[] FACE_OF = new int[6];          // ось +x, −x, +y, −y, +z, −z → номер грани

    // ---------- свет ----------
    static readonly V3 ROOM = Lin("#e4e2de") * 0.3, SOFTBOX = new V3(3.2, 3.2, 3.2), TABLE = Lin("#c9c2b6") * 0.25;
    static readonly V3 SKY = new(1, 1, 1), GROUND = Lin("#c9c2b6");
    const double KEY = 1.2, EXPOSURE = 0.85;
    static readonly V3 SOFT_POS = new(-6, 4, 9);
    static readonly double SOFT_D = SOFT_POS.Length;
    static readonly V3 SOFT_N = -SOFT_POS * (1 / SOFT_D);
    static readonly V3 SOFT_E1 = V3.Cross(new V3(0, 1, 0), SOFT_N).Norm(), SOFT_E2 = V3.Cross(SOFT_N, SOFT_E1);
    static readonly V3 KEY_W = new V3(-1.5, 3, 4).Norm();          // направление на лампу в «мире» (он наклонён на TILT)
    static readonly V3 KEY_V = TILT * KEY_W;                        // то же у зрителя
    static readonly V3[] KEY_J = new V3[4];                         // лампа чуть размыта — мягкий край тени

    static readonly float[] toLin = new float[256];
    static readonly byte[] toSrgb = new byte[4097];

    static DieRenderer()
    {
        V3[] axes = { new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1) };
        for (int i = 0; i < 6; i++)
        {
            var place = TO_FRONT[i].T;                   // где грань сидит на кубике
            var normal = FN[i] = place * new V3(0, 0, 1);
            FU[i] = place * new V3(1, 0, 0);
            FV[i] = place * new V3(0, 1, 0);
            for (int a = 0; a < 6; a++)
                if (V3.Dot(normal, axes[a]) > 0.9) FACE_OF[a] = i;
        }
        var (t, b) = Basis(KEY_W);
        for (int j = 0; j < 4; j++)
        {
            double ang = (j + 0.5) * Math.PI / 2;
            KEY_J[j] = (KEY_W + t * (0.035 * Math.Cos(ang)) + b * (0.035 * Math.Sin(ang))).Norm();
        }
        for (int i = 0; i < 256; i++) toLin[i] = (float)SrgbToLin(i / 255.0);
        for (int i = 0; i <= 4096; i++) toSrgb[i] = (byte)Math.Round(LinToSrgb(i / 4096.0) * 255);
    }

    static V3 Lin(string hex)
    {
        var c = ColorTranslator.FromHtml(hex);
        return new(SrgbToLin(c.R / 255.0), SrgbToLin(c.G / 255.0), SrgbToLin(c.B / 255.0));
    }
    static double SrgbToLin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    static double LinToSrgb(double c) => c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;

    // окружение: шар-комната, большой софтбокс спереди-слева-сверху и тёплый стол снизу
    static V3 Env(V3 d)
    {
        double dn = V3.Dot(d, SOFT_N);
        if (dn < 0)
        {
            var q = d * (-SOFT_D / dn) - SOFT_POS;
            if (Math.Abs(V3.Dot(q, SOFT_E1)) < 8 && Math.Abs(V3.Dot(q, SOFT_E2)) < 8) return SOFTBOX;
        }
        if (d.Y < 0)
        {
            double t = -8 / d.Y;
            if (Math.Abs(d.X * t) < 20 && Math.Abs(d.Z * t) < 20) return TABLE;
        }
        return ROOM;
    }

    // окружение, заранее размытое под шероховатость поверхности (как PMREM в three.js)
    sealed class EnvMap
    {
        readonly int w, h;
        readonly float[] d;

        public EnvMap(int w, int h, Func<V3, V3> f)
        {
            this.w = w; this.h = h; d = new float[w * h * 3];
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    double phi = ((x + 0.5) / w - 0.5) * 2 * Math.PI, th = (y + 0.5) / h * Math.PI;
                    var c = f(new V3(Math.Sin(th) * Math.Sin(phi), Math.Cos(th), -Math.Sin(th) * Math.Cos(phi)));
                    int i = (y * w + x) * 3;
                    d[i] = (float)c.X; d[i + 1] = (float)c.Y; d[i + 2] = (float)c.Z;
                }
            });
        }

        // координаты направления на развёртке (одни на все карты — тригонометрия один раз)
        public static (double u, double v) UV(V3 dir) =>
            (Math.Atan2(dir.X, -dir.Z) / (2 * Math.PI) + 0.5, Math.Acos(Math.Clamp(dir.Y, -1, 1)) / Math.PI);

        public V3 Sample((double u, double v) at)
        {
            double fx = at.u * w - 0.5, fy = at.v * h - 0.5;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            double tx = fx - x0, ty = fy - y0;
            int xa = ((x0 % w) + w) % w, xb = (xa + 1) % w;
            int ya = Math.Clamp(y0, 0, h - 1), yb = Math.Clamp(y0 + 1, 0, h - 1);
            V3 At(int x, int y) { int i = (y * w + x) * 3; return new(d[i], d[i + 1], d[i + 2]); }
            return V3.Lerp(V3.Lerp(At(xa, ya), At(xb, ya), tx), V3.Lerp(At(xa, yb), At(xb, yb), tx), ty);
        }
    }

    static double Radical(int i, int b)
    {
        double r = 0, f = 1.0 / b;
        for (; i > 0; i /= b, f /= b) r += f * (i % b);
        return r;
    }
    static (V3 t, V3 b) Basis(V3 n)
    {
        var t = V3.Cross(Math.Abs(n.Y) < 0.999 ? new V3(0, 1, 0) : new V3(1, 0, 0), n).Norm();
        return (t, V3.Cross(n, t));
    }

    // свёртка окружения с бликом GGX заданной шероховатости + общее размытие 0.08 рад, как в PMREM
    static V3 Prefilter(V3 n, double rough, int count)
    {
        V3 sum = default; double wsum = 0, a2 = Math.Pow(rough, 4);
        var (t0, b0) = Basis(n);
        for (int i = 0; i < count; i++)
        {
            double u1 = Math.Max(1e-6, Radical(i + 1, 3)), u2 = Radical(i + 1, 5), r = Math.Sqrt(-2 * Math.Log(u1)) * 0.08;
            var nn = (n + t0 * (r * Math.Cos(2 * Math.PI * u2)) + b0 * (r * Math.Sin(2 * Math.PI * u2))).Norm();
            var (t, b) = Basis(nn);
            double e1 = (i + 0.5) / count, e2 = Radical(i, 2), phi = 2 * Math.PI * e1;
            double ct = Math.Sqrt((1 - e2) / (1 + (a2 - 1) * e2)), st = Math.Sqrt(1 - ct * ct);
            var hv = t * (st * Math.Cos(phi)) + b * (st * Math.Sin(phi)) + nn * ct;
            var l = hv * (2 * V3.Dot(nn, hv)) - nn;
            double ndl = V3.Dot(nn, l);
            if (ndl > 0) { sum += Env(l) * ndl; wsum += ndl; }
        }
        return sum * (1 / wsum);
    }

    // рассеянный свет окружения для нормали n (среднее по полусфере с весом косинуса)
    static V3 Irradiance(V3 n, int count)
    {
        V3 sum = default;
        var (t, b) = Basis(n);
        for (int i = 0; i < count; i++)
        {
            double e1 = (i + 0.5) / count, e2 = Radical(i, 2), r = Math.Sqrt(e2), phi = 2 * Math.PI * e1;
            sum += Env(t * (r * Math.Cos(phi)) + b * (r * Math.Sin(phi)) + n * Math.Sqrt(1 - e2));
        }
        return sum * (1.0 / count);
    }

    readonly EnvMap coatEnv, bodyEnv, inkEnv, diffEnv;

    // ---------- текстуры граней ----------
    // цифры: [грань][уровень детализации], на тексель четыре байта — заливка цифры, глубина канавки
    // и наклон её стенок по x и y (128 — ровно)
    readonly byte[][][] masks = new byte[6][][];
    byte[][] body;                                   // корпус с зерном, sRGB, по уровням детализации
    readonly byte[] grain = new byte[TEX * TEX * 4]; // зерно и пятна, как у кости (RGBA с предумноженной альфой)
    V3 ink;                                          // цвет цифр, sRGB 0..255

    public DieRenderer()
    {
        coatEnv = new EnvMap(128, 64, n => Prefilter(n, 0.06, 192));   // лак
        bodyEnv = new EnvMap(64, 32, n => Prefilter(n, 0.45, 192));    // корпус
        inkEnv = new EnvMap(32, 16, n => Prefilter(n, 0.8, 256));      // матовые цифры
        diffEnv = new EnvMap(32, 16, n => Irradiance(n, 256));

        var rnd = new Random();
        var g = new float[TEX * TEX * 4];
        for (int i = 0; i < TEX * TEX; i++)
        {
            float v = rnd.NextDouble() < 0.5 ? 0 : 1, a = (float)(rnd.NextDouble() * 10 / 255);   // мелкое зерно, еле заметное
            g[i * 4] = g[i * 4 + 1] = g[i * 4 + 2] = v * a; g[i * 4 + 3] = a;
        }
        for (int n = 0; n < 14; n++)                  // мягкие пятна: кость окрашена чуть неровно
        {
            double cx = rnd.NextDouble() * TEX, cy = rnd.NextDouble() * TEX, r = TEX * (0.15 + rnd.NextDouble() * 0.3);
            var col = rnd.NextDouble() < 0.5 ? new V3(150, 120, 80) : new V3(255, 255, 250);
            for (int y = Math.Max(0, (int)(cy - r)); y < Math.Min(TEX, (int)(cy + r) + 1); y++)
                for (int x = Math.Max(0, (int)(cx - r)); x < Math.Min(TEX, (int)(cx + r) + 1); x++)
                {
                    double d = Math.Sqrt((x + 0.5 - cx) * (x + 0.5 - cx) + (y + 0.5 - cy) * (y + 0.5 - cy)) / r;
                    if (d >= 1) continue;
                    float a = (float)(0.06 * (1 - d));
                    int i = (y * TEX + x) * 4;
                    g[i] = (float)(col.X / 255 * a + g[i] * (1 - a));
                    g[i + 1] = (float)(col.Y / 255 * a + g[i + 1] * (1 - a));
                    g[i + 2] = (float)(col.Z / 255 * a + g[i + 2] * (1 - a));
                    g[i + 3] = a + g[i + 3] * (1 - a);
                }
        }
        for (int i = 0; i < g.Length; i++) grain[i] = (byte)Math.Round(g[i] * 255);
    }

    public void SetStyle(DieStyle s)
    {
        ink = new(s.Ink.R, s.Ink.G, s.Ink.B);
        var top = new byte[TEX * TEX * 3];
        for (int i = 0; i < TEX * TEX; i++)
        {
            double a = grain[i * 4 + 3] / 255.0 * s.Grain;
            top[i * 3] = (byte)Math.Round(s.Body.R * (1 - a) + grain[i * 4] * s.Grain);
            top[i * 3 + 1] = (byte)Math.Round(s.Body.G * (1 - a) + grain[i * 4 + 1] * s.Grain);
            top[i * 3 + 2] = (byte)Math.Round(s.Body.B * (1 - a) + grain[i * 4 + 2] * s.Grain);
        }
        body = Mips(top, 3);
    }

    // уровни детализации: каждый следующий вдвое меньше — мелкий кубик без ряби в цифрах
    static byte[][] Mips(byte[] top, int ch)
    {
        var levels = new byte[LEVELS][];
        levels[0] = top;
        for (int l = 1, s = TEX / 2; l < LEVELS; l++, s /= 2)
        {
            var src = levels[l - 1]; var dst = new byte[s * s * ch]; int ss = s * 2;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                    for (int c = 0; c < ch; c++)
                        dst[(y * s + x) * ch + c] = (byte)((src[(2 * y * ss + 2 * x) * ch + c] + src[(2 * y * ss + 2 * x + 1) * ch + c]
                            + src[((2 * y + 1) * ss + 2 * x) * ch + c] + src[((2 * y + 1) * ss + 2 * x + 1) * ch + c] + 2) / 4);
            levels[l] = dst;
        }
        return levels;
    }

    // число, которое вверх ногами читается как другое число 1–100
    static bool FlipsTo(string text)
    {
        var turn = new Dictionary<char, char> { ['0'] = '0', ['1'] = '1', ['6'] = '9', ['8'] = '8', ['9'] = '6' };
        if (!text.All(turn.ContainsKey)) return false;
        var r = new string(text.Reverse().Select(c => turn[c]).ToArray());
        return r != text && r[0] != '0' && int.Parse(r) is >= 1 and <= 100;
    }

    static FontFamily numFont;
    public void SetNumbers(int[] nums)
    {
        numFont ??= Fonts.Family("Segoe UI Semibold", "Segoe UI");
        using var bmp = new Bitmap(TEX, TEX, PixelFormat.Format32bppArgb);
        using var gr = Graphics.FromImage(bmp);
        gr.SmoothingMode = SmoothingMode.HighQuality;
        for (int i = 0; i < 6; i++)
        {
            gr.Clear(Color.Transparent);
            string text = nums[i].ToString();
            float fs = TEX * 0.34f;
            GraphicsPath Path(float size)
            {
                var p = new GraphicsPath();
                p.AddString(text, numFont, 0, size, new PointF(0, 0), StringFormat.GenericTypographic);
                return p;
            }
            var path = Path(fs);
            var bb = path.GetBounds();
            if (bb.Width > TEX * 0.6f) { fs *= TEX * 0.6f / bb.Width; path.Dispose(); path = Path(fs); bb = path.GetBounds(); }
            using (var mt = new Matrix())
            {
                mt.Translate(TEX / 2f - (bb.X + bb.Width / 2), TEX / 2f - (bb.Y + bb.Height / 2));
                path.Transform(mt);
            }
            gr.FillPath(Brushes.White, path);
            path.Dispose();
            // черта снизу, как на настоящих костях: 6 и 9, 68 и 89 вверх ногами не перепутать
            if (FlipsTo(text))
            {
                float lw = bb.Width * 0.7f, lh = fs * 0.055f, y = TEX / 2f + bb.Height / 2 + fs * 0.12f;
                using var line = Shapes.Round(new RectangleF(TEX / 2f - lw / 2, y, lw, lh), lh / 2);
                gr.FillPath(Brushes.White, line);
            }
            var top = new byte[TEX * TEX];
            var data = bmp.LockBits(new Rectangle(0, 0, TEX, TEX), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < TEX; y++)
            {
                byte* row = (byte*)data.Scan0 + y * data.Stride;
                for (int x = 0; x < TEX; x++) top[y * TEX + x] = row[x * 4 + 3];
            }
            bmp.UnlockBits(data);
            masks[i] = Engrave(top);
        }
    }

    // канавка под цифрой: глубина — размытая маска, стенки идут по обе стороны контура. Краска — строго по цифре,
    // так что она не худеет, а снаружи у контура видна кромка: тень с одной стороны и блик с другой
    static byte[][] Engrave(byte[] top)
    {
        var blur = new float[TEX * TEX];
        for (int i = 0; i < blur.Length; i++) blur[i] = top[i] / 255f;
        for (int pass = 0; pass < 2; pass++) { BoxBlur(blur, true); BoxBlur(blur, false); }
        var depth = new byte[TEX * TEX];
        for (int i = 0; i < depth.Length; i++)
        {
            double t = Math.Clamp((blur[i] - 0.12) / 0.7, 0, 1);
            depth[i] = (byte)Math.Round(255 * t * t * (3 - 2 * t));
        }
        byte[][] mm = Mips(top, 1), dm = Mips(depth, 1), levels = new byte[LEVELS][];
        for (int l = 0, s = TEX; l < LEVELS; l++, s /= 2)
        {
            var d = dm[l]; var o = levels[l] = new byte[s * s * 4];
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    int i = y * s + x;
                    double gx = (d[y * s + Math.Min(x + 1, s - 1)] - d[y * s + Math.Max(x - 1, 0)]) / 510.0;
                    double gy = (d[Math.Min(y + 1, s - 1) * s + x] - d[Math.Max(y - 1, 0) * s + x]) / 510.0;
                    o[i * 4] = mm[l][i]; o[i * 4 + 1] = d[i];
                    o[i * 4 + 2] = (byte)Math.Clamp(Math.Round(128 + gx * 127), 1, 255);
                    o[i * 4 + 3] = (byte)Math.Clamp(Math.Round(128 + gy * 127), 1, 255);
                }
        }
        return levels;
    }

    const int GROOVE = 5;                            // ширина стенки канавки, текселей
    static void BoxBlur(float[] a, bool horiz)
    {
        Parallel.For(0, TEX, line =>
        {
            Span<float> src = stackalloc float[TEX];
            for (int j = 0; j < TEX; j++) src[j] = a[horiz ? line * TEX + j : j * TEX + line];
            float sum = 0; int n = 0;
            for (int j = -GROOVE; j < TEX; j++)
            {
                if (j + GROOVE < TEX) { sum += src[j + GROOVE]; n++; }
                if (j - GROOVE - 1 >= 0) { sum -= src[j - GROOVE - 1]; n--; }
                if (j >= 0) a[horiz ? line * TEX + j : j * TEX + line] = sum / n;
            }
        });
    }

    // ---------- геометрия ----------
    // пересечение луча с кубом со скруглёнными рёбрами (формула Иниго Квилеса); −1, если мимо
    static double Box(V3 ro, V3 rd)
    {
        double rx = Math.Abs(rd.X) < 1e-12 ? 1e-12 : rd.X, ry = Math.Abs(rd.Y) < 1e-12 ? 1e-12 : rd.Y, rz = Math.Abs(rd.Z) < 1e-12 ? 1e-12 : rd.Z;
        double mx = 1 / rx, my = 1 / ry, mz = 1 / rz;
        double kx = Math.Abs(mx) * 0.5, ky = Math.Abs(my) * 0.5, kz = Math.Abs(mz) * 0.5;
        double nx = mx * ro.X, ny = my * ro.Y, nz = mz * ro.Z;
        double tN = Math.Max(Math.Max(-nx - kx, -ny - ky), -nz - kz);
        double tF = Math.Min(Math.Min(-nx + kx, -ny + ky), -nz + kz);
        if (tN > tF || tF < 0) return -1;
        double t = tN;
        double px = ro.X + t * rx, py = ro.Y + t * ry, pz = ro.Z + t * rz;
        double sx = px < 0 ? -1 : 1, sy = py < 0 ? -1 : 1, sz = pz < 0 ? -1 : 1;
        double ox = ro.X * sx, oy = ro.Y * sy, oz = ro.Z * sz, dx = rx * sx, dy = ry * sy, dz = rz * sz;
        px = px * sx - HALF; py = py * sy - HALF; pz = pz * sz - HALF;
        if (Math.Min(Math.Min(Math.Max(px, py), Math.Max(py, pz)), Math.Max(pz, px)) < 0) return t;   // плоская часть грани

        double cx = ox - HALF, cy = oy - HALF, cz = oz - HALF;
        double ddx = dx * dx, ddy = dy * dy, ddz = dz * dz, oox = cx * cx, ooy = cy * cy, ooz = cz * cz;
        double odx = cx * dx, ody = cy * dy, odz = cz * dz, ra2 = RAD * RAD;
        t = 1e20;
        {   // угол
            double b = odx + ody + odz, c = oox + ooy + ooz - ra2, h = b * b - c;
            if (h > 0) t = -b - Math.Sqrt(h);
        }
        void Edge(double a, double b, double c, double o, double d)
        {
            double h = b * b - a * c;
            if (h <= 0) return;
            h = (-b - Math.Sqrt(h)) / a;
            if (h > 0 && h < t && Math.Abs(o + d * h) < HALF) t = h;
        }
        Edge(ddy + ddz, ody + odz, ooy + ooz - ra2, ox, dx);
        Edge(ddz + ddx, odz + odx, ooz + oox - ra2, oy, dy);
        Edge(ddx + ddy, odx + ody, oox + ooy - ra2, oz, dz);
        return t > 1e19 ? -1 : t;
    }

    // проходит ли луч рядом с кубиком вообще (дешёвая проверка перед точной)
    static bool NearBall(V3 o, V3 d)
    {
        double b = V3.Dot(o, d), c = V3.Dot(o, o) - BOUND * BOUND;
        return !(c > 0 && b > 0) && b * b - c >= 0;
    }

    // ---------- материал ----------
    // гравировка в точке: заливка 0..1, глубина 0..1 и наклон глубины по u и v (на единицу грани)
    struct Groove { public double Ink, Depth, Du, Dv; }

    void Fetch(int face, int lvl, double u, double v, double w, ref Groove g)
    {
        int s = TEX >> lvl; var m = masks[face][lvl];
        double fx = u * s - 0.5, fy = (1 - v) * s - 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        double tx = fx - x0, ty = fy - y0;
        int xa = Math.Clamp(x0, 0, s - 1), xb = Math.Clamp(x0 + 1, 0, s - 1), ya = Math.Clamp(y0, 0, s - 1), yb = Math.Clamp(y0 + 1, 0, s - 1);
        int i00 = (ya * s + xa) * 4, i01 = (ya * s + xb) * 4, i10 = (yb * s + xa) * 4, i11 = (yb * s + xb) * 4;
        double w00 = (1 - tx) * (1 - ty) * w, w01 = tx * (1 - ty) * w, w10 = (1 - tx) * ty * w, w11 = tx * ty * w;
        double C(int c) => m[i00 + c] * w00 + m[i01 + c] * w01 + m[i10 + c] * w10 + m[i11 + c] * w11;
        g.Ink += C(0) / 255; g.Depth += C(1) / 255;
        g.Du += (C(2) - 128 * w) / 127 * s; g.Dv -= (C(3) - 128 * w) / 127 * s;   // в текстуре y вниз, а v вверх
    }

    // как анизотропная фильтрация у видеокарты: детализация — по узкой стороне пятна пикселя на грани,
    // а вдоль длинной (на наклонной грани) несколько выборок. Иначе на верхней и боковой гранях цифры рябят
    Groove SampleGroove(int face, double u, double v, double lod, double au, double av, int taps)
    {
        lod = Math.Clamp(lod, 0, LEVELS - 1);
        int l0 = Math.Min((int)lod, LEVELS - 2); double f = lod - l0;
        var g = new Groove();
        for (int i = 0; i < taps; i++)
        {
            double o = (i + 0.5) / taps - 0.5, uu = u + au * o, vv = v + av * o;
            Fetch(face, l0, uu, vv, (1 - f) / taps, ref g);
            if (f > 0) Fetch(face, l0 + 1, uu, vv, f / taps, ref g);
        }
        return g;
    }

    V3 SampleBody(int lvl, double u, double v)
    {
        int s = TEX >> lvl; var m = body[lvl];
        double fx = u * s - 0.5, fy = (1 - v) * s - 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        double tx = fx - x0, ty = fy - y0;
        int xa = Math.Clamp(x0, 0, s - 1), xb = Math.Clamp(x0 + 1, 0, s - 1), ya = Math.Clamp(y0, 0, s - 1), yb = Math.Clamp(y0 + 1, 0, s - 1);
        V3 At(int x, int y) { int i = (y * s + x) * 3; return new(m[i], m[i + 1], m[i + 2]); }
        return V3.Lerp(V3.Lerp(At(xa, ya), At(xb, ya), tx), V3.Lerp(At(xa, yb), At(xb, yb), tx), ty);
    }

    static double ToLin(double c255)
    {
        int i = Math.Clamp((int)c255, 0, 254); double f = c255 - i;
        return toLin[i] + (toLin[i + 1] - toLin[i]) * f;
    }

    // доля отражения для окружения (приближение Карица, как в three.js)
    static (double a, double b) EnvBrdf(double ndv, double r)
    {
        double rx = -r + 1, ry = -0.0275 * r + 0.0425, rz = -0.572 * r + 1.04, rw = 0.022 * r - 0.04;
        double a004 = Math.Min(rx * rx, Math.Pow(2, -9.28 * ndv)) * rx + ry;
        return (-1.04 * a004 + rz, 1.04 * a004 + rw);
    }

    static double Ggx(double ndl, double ndv, double ndh, double vdh, double rough, double f0)
    {
        double a2 = Math.Pow(Math.Max(rough, 0.0525), 4);
        double den = ndh * ndh * (a2 - 1) + 1, d = a2 / (Math.PI * den * den);
        double vis = 0.5 / (ndl * Math.Sqrt(a2 + (1 - a2) * ndv * ndv) + ndv * Math.Sqrt(a2 + (1 - a2) * ndl * ndl));
        double f = f0 + (1 - f0) * Math.Pow(1 - vdh, 5);
        return f * vis * d;
    }

    // нейтральный тонмаппинг Khronos (NeutralToneMapping в three.js)
    static V3 Neutral(V3 c)
    {
        const double start = 0.8 - 0.04, desat = 0.15;
        double x = Math.Min(c.X, Math.Min(c.Y, c.Z)), off = x < 0.08 ? x - 6.25 * x * x : 0.04;
        c -= new V3(off, off, off);
        double peak = c.Max;
        if (peak < start) return c;
        double d = 1 - start, np = 1 - d * d / (peak + d - start);
        c *= np / peak;
        double g = 1 - 1 / (desat * (peak - np) + 1);
        return V3.Lerp(c, new V3(np, np, np), g);
    }

    const double DEEP = 0.016;                       // глубина гравировки (ребро кубика — 1)

    // цвет точки кубика (у зрителя): p — точка в осях кубика, view — направление на камеру,
    // keyL — направление на лампу в осях кубика, foot — сколько грани приходится на пиксель
    V3 Shade(V3 p, M3 m, M3 mT, V3 keyL, V3 view, double foot)
    {
        var nl = new V3(Pow23(p.X * 2), Pow23(p.Y * 2), Pow23(p.Z * 2)).Norm();
        double ax = Math.Abs(p.X), ay = Math.Abs(p.Y), az = Math.Abs(p.Z);
        int axis = ax >= ay && ax >= az ? (p.X > 0 ? 0 : 1) : ay >= az ? (p.Y > 0 ? 2 : 3) : (p.Z > 0 ? 4 : 5);
        int face = FACE_OF[axis];
        double u = V3.Dot(p, FU[face]) + 0.5, v = V3.Dot(p, FV[face]) + 0.5;

        // пятно пикселя на грани: поперёк взгляда — foot, вдоль наклона — foot / cos
        var lv = mT * view;
        double pu = V3.Dot(lv, FU[face]), pv = V3.Dot(lv, FV[face]), pl = Math.Sqrt(pu * pu + pv * pv);
        double stretch = 1 / Math.Max(Math.Abs(V3.Dot(lv, FN[face])), 0.1);
        int taps = Math.Min((int)Math.Ceiling(stretch - 0.05), 8);
        double lod = Math.Log2(foot * TEX), au = 0, av = 0;
        if (taps > 1 && pl > 1e-6) { au = pu / pl * foot * stretch; av = pv / pl * foot * stretch; }
        else taps = 1;
        var gr = SampleGroove(face, u, v, lod, au, av, taps);
        double mask = gr.Ink;
        int lvl = Math.Clamp((int)Math.Round(lod), 0, LEVELS - 1);

        // стенки канавки наклоняют нормаль внутрь цифры. На мелком кубике стенки мельче пикселя
        // и только мылят цифру — там рельеф слабее
        double relief = Math.Clamp(3.6 - lod, 0.3, 1);
        nl = (nl + (FU[face] * gr.Du + FV[face] * gr.Dv) * (DEEP * relief)).Norm();
        // у стенки, что ближе к лампе, канавка в её тени; в глубине и рассеянного света меньше
        double shadow = 0, ao = 1 - 0.2 * gr.Depth * relief;
        double ln = V3.Dot(keyL, FN[face]);
        if (gr.Depth > 0.02 && ln > 0.05)
        {
            double k = DEEP * gr.Depth / ln;
            var sh = SampleGroove(face, u + V3.Dot(keyL, FU[face]) * k, v + V3.Dot(keyL, FV[face]) * k, lod, 0, 0, 1);
            shadow = Math.Clamp((gr.Depth - sh.Depth) * 2, 0, 1) * relief;
            ao *= 1 - 0.35 * shadow;
        }

        var srgb = V3.Lerp(SampleBody(lvl, u, v), ink, mask);
        var albedo = new V3(ToLin(srgb.X), ToLin(srgb.Y), ToLin(srgb.Z));
        double coat = 0.3 * (1 - mask), rough = 0.45 + 0.35 * mask;

        var n = m * nl;
        double ndv = Math.Max(V3.Dot(n, view), 1e-4);
        var r = n * (2 * ndv) - view;

        // окружение: рассеянный свет и мягкий блик корпуса
        var (ea, eb) = EnvBrdf(ndv, rough);
        double spec = 0.04 * ea + eb;
        var nuv = EnvMap.UV(n); var ruv = EnvMap.UV(r);
        var color = albedo * diffEnv.Sample(nuv) * (1 - spec) + V3.Lerp(bodyEnv.Sample(ruv), inkEnv.Sample(ruv), mask) * spec;
        // рассеянная подсветка теней: небо и стол
        color += albedo * V3.Lerp(GROUND, SKY, 0.5 * n.Y + 0.5) * (1 / Math.PI);
        color *= ao;
        // лампа
        double ndl = V3.Dot(n, KEY_V), irr = 0, ndh = 0, vdh = 0;
        if (ndl > 0)
        {
            irr = ndl * KEY * (1 - 0.85 * shadow);
            var h = (KEY_V + view).Norm();
            ndh = Math.Max(V3.Dot(n, h), 0); vdh = Math.Max(V3.Dot(view, h), 0);
            color += albedo * (irr / Math.PI) + new V3(1, 1, 1) * (irr * Ggx(ndl, ndv, ndh, vdh, rough, 0.04));
        }
        // лак: чёткое отражение софтбокса поверх всего
        if (coat > 0)
        {
            double fcc = 0.04 + 0.96 * Math.Pow(1 - ndv, 5);
            var (ca, cb) = EnvBrdf(ndv, 0.06);
            var cc = coatEnv.Sample(ruv) * (0.04 * ca + cb);
            if (irr > 0) cc += new V3(1, 1, 1) * (irr * Ggx(ndl, ndv, ndh, vdh, 0.06, 0.04));
            color = color * (1 - coat * fcc) + cc * coat;
        }
        var t = Neutral(color * EXPOSURE);
        return new(Out(t.X), Out(t.Y), Out(t.Z));
    }

    static double Out(double c) => toSrgb[(int)(Math.Clamp(c, 0, 1) * 4096)];

    // ---------- кадр ----------
    // половина ядер: кадр и так быстрый, а лишние потоки только жгут процессор в ожидании работы
    static readonly ParallelOptions Par = new() { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) };
    static readonly (double x, double y)[] SUB = { (0.375, 0.125), (0.875, 0.375), (0.125, 0.625), (0.625, 0.875) };

    // рисует кубик с тенью в px (BGRA, предумноженная альфа) размером w×h; k — пикселей на ребро кубика;
    // m — поворот кубика у зрителя, hop — высота прыжка 0..1, slide — сдвиг вбок по столу (в рёбрах кубика).
    // Возвращает область, куда рисовали.
    public Rectangle Render(uint* px, int w, int h, double k, M3 m, double hop, double slide = 0)
    {
        var o = new V3(0, 0, DIST);
        var cw = new V3(slide, hop * 0.44, 0);          // центр кубика в «мире»
        var cv = TILT * cw;                              // и у зрителя
        var mT = m.T;
        var rw = TILT.T * m; var rwT = rw.T;             // поворот кубика в «мире»
        var lo = mT * (o - cv);                          // камера в осях кубика
        var keyL = mT * KEY_V;                           // лампа в осях кубика
        double perT = 1 / (k * DIST);                    // размер пикселя на расстоянии 1 от камеры
        var floorN = TILT * new V3(0, 1, 0);
        var floorP = TILT * new V3(0, -0.5, 0);
        double blobR = 0.8 * (1 - 0.4 * hop), blobA = 1 - 0.6 * hop;

        // где что может быть на экране: рамки кубика, пятна под ним и тени от лампы — лучи пускаем только туда
        RectangleF Frame(IEnumerable<V3> pts)
        {
            double fx0 = double.MaxValue, fy0 = double.MaxValue, fx1 = double.MinValue, fy1 = double.MinValue;
            foreach (var p in pts)
            {
                double sc = k * DIST / (DIST - p.Z), sx = w / 2.0 + p.X * sc, sy = h / 2.0 - p.Y * sc;
                fx0 = Math.Min(fx0, sx); fx1 = Math.Max(fx1, sx); fy0 = Math.Min(fy0, sy); fy1 = Math.Max(fy1, sy);
            }
            return RectangleF.FromLTRB((float)fx0 - 2, (float)fy0 - 2, (float)fx1 + 3, (float)fy1 + 3);
        }
        var corners = new List<V3>();
        foreach (var cx in new[] { -0.5, 0.5 }) foreach (var cy in new[] { -0.5, 0.5 }) foreach (var cz in new[] { -0.5, 0.5 })
            corners.Add(new V3(cx, cy, cz));
        var cubeBox = Frame(corners.Select(c => cv + m * c));
        var blobBox = Frame(new[] { (-1, -1), (-1, 1), (1, -1), (1, 1) }.Select(q => TILT * new V3(slide + q.Item1 * blobR, -0.5, q.Item2 * blobR)));
        var shadowBox = Frame(corners.Select(c =>
        {
            var p = cw + rw * c;                         // угол кубика в «мире» → по лучу лампы на пол
            return TILT * (p - KEY_W * ((p.Y + 0.5) / KEY_W.Y));
        }));
        shadowBox.Inflate((float)(0.06 * k), (float)(0.06 * k));   // размытый край тени
        var all = RectangleF.Union(cubeBox, RectangleF.Union(blobBox, shadowBox));
        var rect = Rectangle.FromLTRB(Math.Max(0, (int)all.Left), Math.Max(0, (int)all.Top), Math.Min(w, (int)all.Right), Math.Min(h, (int)all.Bottom));
        float cbL = cubeBox.Left, cbT = cubeBox.Top, cbR = cubeBox.Right, cbB = cubeBox.Bottom;
        float bbL = blobBox.Left, bbT = blobBox.Top, bbR = blobBox.Right, bbB = blobBox.Bottom;
        float sbL = shadowBox.Left, sbT = shadowBox.Top, sbR = shadowBox.Right, sbB = shadowBox.Bottom;

        var bits = (IntPtr)px;
        V3 Ray(double sx, double sy) => new V3((sx - w / 2.0) / k, -(sy - h / 2.0) / k, -DIST).Norm();
        Parallel.For(rect.Top, rect.Bottom, Par, y =>
        {
            uint* row = (uint*)bits + y * w;
            Span<double> hitT = stackalloc double[4];
            bool rowCube = y >= cbT && y <= cbB, rowBlob = y >= bbT && y <= bbB, rowShadow = y >= sbT && y <= sbB;
            for (int x = rect.Left; x < rect.Right; x++)
            {
                bool inCube = rowCube && x >= cbL && x <= cbR, inBlob = rowBlob && x >= bbL && x <= bbR, inShadow = rowShadow && x >= sbL && x <= sbR;
                if (!inCube && !inBlob && !inShadow) continue;
                // сглаживание: четыре луча на пиксель; если все попали в кубик — красим один раз по центру
                double r = 0, g = 0, b = 0, a = 0;
                int hits = 0;
                for (int s = 0; s < 4; s++)
                {
                    if (!inCube) { hitT[s] = -1; continue; }
                    var ld = mT * Ray(x + SUB[s].x, y + SUB[s].y);
                    hitT[s] = NearBall(lo, ld) ? Box(lo, ld) : -1;
                    if (hitT[s] > 0) hits++;
                }
                if (hits == 4)
                {
                    var d = Ray(x + 0.5, y + 0.5); var ld = mT * d;
                    double t = Box(lo, ld);
                    if (t <= 0) { d = Ray(x + SUB[0].x, y + SUB[0].y); ld = mT * d; t = hitT[0]; }
                    var c = Shade(lo + ld * t, m, mT, keyL, -d, t * perT);
                    row[x] = 0xFF000000u | (uint)c.X << 16 | (uint)c.Y << 8 | (uint)c.Z;
                    continue;
                }
                for (int s = 0; s < 4; s++)
                {
                    var d = Ray(x + SUB[s].x, y + SUB[s].y);
                    if (hitT[s] > 0)
                    {
                        var c = Shade(lo + mT * d * hitT[s], m, mT, keyL, -d, hitT[s] * perT);
                        r += c.X; g += c.Y; b += c.Z; a += 255;
                        continue;
                    }
                    // пол: мягкое пятно под кубиком и тень от лампы (сам пол прозрачный)
                    if (!inBlob && !inShadow) continue;
                    double dn = V3.Dot(d, floorN);
                    if (dn >= 0) continue;
                    var pw = TILT.T * (o + d * (V3.Dot(floorP - o, floorN) / dn));
                    double bx = pw.X - slide;
                    if (Math.Abs(bx) > 2 || Math.Abs(pw.Z) > 2) continue;
                    double fa = 0, dist = Math.Sqrt(bx * bx + pw.Z * pw.Z) / blobR;
                    if (dist < 1) fa = (dist < 0.45 ? 0.5 - 0.3 * dist / 0.45 : 0.2 * (1 - dist) / 0.55) * blobA;
                    if (inShadow)
                    {
                        var so = rwT * (pw - cw); var sd = rwT * KEY_J[s];
                        if (NearBall(so, sd) && Box(so, sd) > 0) fa += 0.12 * (1 - fa);
                    }
                    a += fa * 255;
                }
                if (a <= 0) continue;
                uint A = (uint)Math.Round(a / 4), R = (uint)Math.Round(r / 4), G = (uint)Math.Round(g / 4), B = (uint)Math.Round(b / 4);
                row[x] = A << 24 | R << 16 | G << 8 | B;
            }
        });
        return rect;
    }
}
