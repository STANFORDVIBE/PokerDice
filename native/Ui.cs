// Общее для рисования: цвета интерфейса, шрифты, скруглённые прямоугольники, мягкая тень.
using System.Drawing.Drawing2D;

namespace PokerDice;

static class Palette
{
    public static readonly Color Die = Hex("#f4f3ef"), Ink = Hex("#17191c"), Panel = Hex("#1c1e22"),
        PanelLine = Color.FromArgb(20, 255, 255, 255), Text = Hex("#e9e7e2"), Muted = Hex("#8d9096"),
        Gold = Hex("#e2b85c"), Danger = Hex("#f0a39b"), Hover = Color.FromArgb(18, 255, 255, 255);
    public static Color Hex(string h) => ColorTranslator.FromHtml(h);
}

static class Fonts
{
    // первый установленный шрифт из списка
    public static FontFamily Family(params string[] names)
    {
        foreach (var n in names)
            try { return new FontFamily(n); } catch (ArgumentException) { }
        return FontFamily.GenericSansSerif;
    }

    static FontFamily ui, uiBold, num;
    public static FontFamily Ui => ui ??= Family("Segoe UI");
    public static FontFamily UiSemibold => uiBold ??= Family("Segoe UI Semibold", "Segoe UI");
    public static FontFamily Num => num ??= Family("Bahnschrift SemiBold", "Bahnschrift", "Segoe UI Semibold");
}

static class Shapes
{
    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2);
        if (rad <= 0) { p.AddRectangle(r); return p; }
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // мягкая тень под скруглённым прямоугольником: рисуем силуэт и размываем его (как box-shadow)
    public static unsafe void Shadow(LayeredBuffer buf, RectangleF r, float rad, float blur, double opacity)
    {
        int w = buf.Width, h = buf.Height;
        var a = new float[w * h];
        using (var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var p = Round(r, rad);
                g.FillPath(Brushes.Black, p);
            }
            var d = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, bmp.PixelFormat);
            for (int y = 0; y < h; y++)
            {
                byte* row = (byte*)d.Scan0 + y * d.Stride;
                for (int x = 0; x < w; x++) a[y * w + x] = row[x * 4 + 3] / 255f;
            }
            bmp.UnlockBits(d);
        }
        // три прохода размытия прямоугольником ≈ гауссово размытие с сигмой blur/2
        double sigma = blur / 2;
        int k = Math.Max(1, (int)Math.Round((Math.Sqrt(4 * sigma * sigma + 1) - 1) / 2));
        var tmp = new float[w * h];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxBlur(a, tmp, w, h, k, true);
            BoxBlur(tmp, a, w, h, k, false);
        }
        var px = buf.Pixels;
        for (int i = 0; i < px.Length; i++)
        {
            uint al = (uint)Math.Round(Math.Clamp(a[i] * opacity, 0, 1) * 255);
            px[i] = al << 24;
        }
    }

    static void BoxBlur(float[] src, float[] dst, int w, int h, int k, bool horiz)
    {
        int n = horiz ? w : h, lines = horiz ? h : w;
        for (int l = 0; l < lines; l++)
        {
            float sum = 0; int cnt = 2 * k + 1;
            float At(int i) => i < 0 || i >= n ? 0 : horiz ? src[l * w + i] : src[i * w + l];
            for (int i = -k; i <= k; i++) sum += At(i);
            for (int i = 0; i < n; i++)
            {
                if (horiz) dst[l * w + i] = sum / cnt; else dst[i * w + l] = sum / cnt;
                sum += At(i + k + 1) - At(i - k);
            }
        }
    }
}
