// Меню по правому клику: отдельное маленькое окно, поэтому всегда целиком на экране —
// не влезает вправо или вниз от курсора, открывается влево или вверх, как обычное меню Windows.
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace PokerDice;

sealed class MenuWindow : Form
{
    // размеры в логических пикселях; панель 214×352, вокруг поля под тень 0 6px 18px
    const float W = 214, H = 352, ML = 18, MT = 12, MR = 18, MB = 24;
    const float IN_X = 15, IN_W = 184;                 // ширина содержимого блоков с ползунком и стилями
    static readonly int[] PRESETS = { 25, 33, 50, 66, 75 };

    readonly DieWindow die;
    readonly double dpi;
    readonly LayeredBuffer buf;
    readonly Bitmap bmp;
    readonly uint[] shadow;
    readonly System.Windows.Forms.Timer fade = new() { Interval = 15 };
    readonly long openedAt = Environment.TickCount64;
    readonly Point pos;
    string hover, pressed;
    bool sliding;

    // где что лежит (от левого верхнего угла панели)
    static readonly RectangleF Slider = new(IN_X, 40, IN_W, 16);
    static RectangleF Preset(int i) { float w = (IN_W - 4 * 4) / 5; return new(IN_X + i * (w + 4), 64, w, 21); }
    static RectangleF Swatch(int i) => new(IN_X + i * 39, 157, 32, 32);
    static readonly RectangleF ItemVerdict = new(7, 206, 200, 31), ItemTop = new(7, 239, 200, 31),
        ItemAuto = new(7, 272, 200, 31), ItemClose = new(7, 314, 200, 31);

    public MenuWindow(DieWindow die, Point at)
    {
        this.die = die;
        dpi = die.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = die.TopMost;
        KeyPreview = true;

        buf = new LayeredBuffer((int)Math.Ceiling((ML + W + MR) * dpi), (int)Math.Ceiling((MT + H + MB) * dpi));
        bmp = buf.AsBitmap();
        Shapes.Shadow(buf, new RectangleF((float)(ML * dpi), (float)((MT + 6) * dpi), (float)(W * dpi), (float)(H * dpi)),
            (float)(10 * dpi), (float)(18 * dpi), 0.4);
        shadow = buf.Pixels.ToArray();

        // как обычное меню Windows: не влезает до края экрана — открывается в другую сторону от курсора
        var wa = Screen.FromPoint(at).WorkingArea;
        int pw = (int)(W * dpi), ph = (int)(H * dpi), pad = (int)(4 * dpi);
        int x = at.X + pw + pad > wa.Right ? at.X - pw : at.X;
        int y = at.Y + ph + pad > wa.Bottom ? at.Y - ph : at.Y;
        x = Math.Max(wa.Left + pad, Math.Min(x, wa.Right - pw - pad));
        y = Math.Max(wa.Top + pad, Math.Min(y, wa.Bottom - ph - pad));
        pos = new Point(x - (int)(ML * dpi), y - (int)(MT * dpi));
        Location = pos;
        Size = new Size(buf.Width, buf.Height);

        fade.Tick += (s, e) => { Present(); if (Environment.TickCount64 - openedAt >= 120) fade.Stop(); };
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW; return cp; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Draw();
        fade.Start();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e) => e.Cancel = true;
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Close(); }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        fade.Dispose(); bmp.Dispose(); buf.Dispose();
        base.OnFormClosed(e);
    }

    // меню, уничтоженное без закрытия, не должно дорисовываться по таймеру
    protected override void Dispose(bool disposing)
    {
        if (disposing) fade.Dispose();
        base.Dispose(disposing);
    }

    // плавное появление за 120 мс
    void Present()
    {
        double t = Math.Min(1, (Environment.TickCount64 - openedAt) / 120.0);
        buf.Present(Handle, pos.X, pos.Y, (byte)Math.Round(255 * t));
    }

    // ---------- рисование ----------
    public void Draw()
    {
        shadow.CopyTo(buf.Pixels);
        var p = die.Prefs;
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.ScaleTransform((float)dpi, (float)dpi);
            g.TranslateTransform(ML, MT);

            using (var panel = Shapes.Round(new RectangleF(0, 0, W, H), 10))
            using (var fill = new SolidBrush(Palette.Panel))
            using (var line = new Pen(Palette.PanelLine, 1))
            {
                g.FillPath(fill, panel);
                using var border = Shapes.Round(new RectangleF(0.5f, 0.5f, W - 1, H - 1), 9.5f);
                g.DrawPath(line, border);
            }

            using var ui = new Font(Fonts.Ui, 13, GraphicsUnit.Pixel);
            using var uiSemi = new Font(Fonts.UiSemibold, 13, GraphicsUnit.Pixel);
            using var small = new Font(Fonts.Ui, 11, GraphicsUnit.Pixel);
            using var big = new Font(Fonts.Num, 16, GraphicsUnit.Pixel);
            using var num11 = new Font(Fonts.Num, 11, GraphicsUnit.Pixel);
            using var num15 = new Font(Fonts.Num, 15, GraphicsUnit.Pixel);

            // частота рейза
            DrawText(g, "Частота рейза", ui, Palette.Text, new RectangleF(IN_X, 15, IN_W, 17));
            DrawText(g, p.Freq + "%", big, Palette.Gold, new RectangleF(IN_X, 15, IN_W, 17), StringAlignment.Far);
            float trackY = Slider.Y + Slider.Height / 2;
            using (var t = Shapes.Round(new RectangleF(IN_X, trackY - 2, IN_W, 4), 2)) using (var b = new SolidBrush(Color.FromArgb(36, 255, 255, 255))) g.FillPath(b, t);
            if (p.Freq > 0)
                using (var t = Shapes.Round(new RectangleF(IN_X, trackY - 2, IN_W * p.Freq / 100f, 4), 2)) using (var b = new SolidBrush(Palette.Gold)) g.FillPath(b, t);
            float thumb = IN_X + 7 + (IN_W - 14) * p.Freq / 100f;
            using (var b = new SolidBrush(Palette.Text)) g.FillEllipse(b, thumb - 7, trackY - 7, 14, 14);
            using (var pen = new Pen(Palette.Gold, 2)) g.DrawEllipse(pen, thumb - 6, trackY - 6, 12, 12);

            for (int i = 0; i < PRESETS.Length; i++)
            {
                var r = Preset(i);
                bool on = PRESETS[i] == p.Freq, hot = hover == "p" + i;
                using var shape = Shapes.Round(RectangleF.Inflate(r, -0.5f, -0.5f), 6);
                if (hot) using (var b = new SolidBrush(Palette.Hover)) g.FillPath(b, shape);
                using (var pen = new Pen(on ? Palette.Gold : Palette.PanelLine, 1)) g.DrawPath(pen, shape);
                DrawText(g, PRESETS[i].ToString(), num11, on ? Palette.Gold : hot ? Palette.Text : Palette.Muted, r, StringAlignment.Center);
            }
            string split = p.Freq <= 0 ? "Рейз никогда" : p.Freq >= 100 ? "Рейз всегда" : $"Рейз, если выпало 1–{p.Freq}";
            DrawText(g, split, small, Palette.Muted, new RectangleF(IN_X, 93, IN_W, 14));
            Sep(g, 118);

            // стиль кубика
            DrawText(g, "Стиль кубика", ui, Palette.Text, new RectangleF(IN_X, 132, IN_W, 17));
            DrawText(g, die.Style.Name, uiSemi, Palette.Gold, new RectangleF(IN_X, 132, IN_W, 17), StringAlignment.Far);
            for (int i = 0; i < DieRenderer.Styles.Length; i++)
            {
                var s = DieRenderer.Styles[i];
                var r = Swatch(i);
                if (hover == "s" + i) r.Offset(0, -1);
                using var shape = Shapes.Round(r, 9);
                using (var b = new SolidBrush(s.Body)) g.FillPath(b, shape);
                // объём, как у кнопок-образцов: светлый верх-лево, тёмный низ-право
                using (var b = new LinearGradientBrush(r, Color.FromArgb(60, 255, 255, 255), Color.FromArgb(70, 0, 0, 0), 45f))
                {
                    var blend = new ColorBlend(3)
                    {
                        Colors = new[] { Color.FromArgb(55, 255, 255, 255), Color.FromArgb(0, 128, 128, 128), Color.FromArgb(65, 0, 0, 0) },
                        Positions = new[] { 0f, 0.45f, 1f },
                    };
                    b.InterpolationColors = blend;
                    g.FillPath(b, shape);
                }
                DrawText(g, "7", num15, s.Ink, r, StringAlignment.Center);
                if (s.Id == p.Style)
                    using (var pen = new Pen(Palette.Gold, 2)) using (var border = Shapes.Round(RectangleF.Inflate(r, -1, -1), 8)) g.DrawPath(pen, border);
            }
            Sep(g, 200);

            Item(g, ItemVerdict, "verdict", "Показывать рейз/фолд", ui, p.Verdict);
            Item(g, ItemTop, "top", "Поверх всех окон", ui, p.OnTop);
            Item(g, ItemAuto, "auto", "Автобросок", ui, die.Auto);
            Sep(g, 308);
            Item(g, ItemClose, "close", "Закрыть", ui, null);
            DrawText(g, "Esc×2", small, Palette.Muted, new RectangleF(ItemClose.X + 8, ItemClose.Y, ItemClose.Width - 16, ItemClose.Height), StringAlignment.Far);
        }
        if (IsHandleCreated) Present();
    }

    public Bitmap Snapshot() { Draw(); return new Bitmap(bmp); }   // для проверки (Program --shot)

    void Item(Graphics g, RectangleF r, string id, string label, Font font, bool? check)
    {
        if (hover == id) using (var b = new SolidBrush(Palette.Hover)) using (var s = Shapes.Round(r, 6)) g.FillPath(b, s);
        var box = new RectangleF(r.X + 8, r.Y + (r.Height - 14) / 2, 14, 14);
        if (check is bool on)
        {
            using var shape = Shapes.Round(RectangleF.Inflate(box, -0.75f, -0.75f), 3.5f);
            if (on)
            {
                using (var b = new SolidBrush(Palette.Gold)) using (var full = Shapes.Round(box, 4)) g.FillPath(b, full);
                using var pen = new Pen(Palette.Ink, 2) { StartCap = LineCap.Square, EndCap = LineCap.Square, LineJoin = LineJoin.Miter };
                g.DrawLines(pen, new[] { new PointF(box.X + 3.6f, box.Y + 7f), new PointF(box.X + 6f, box.Y + 9.3f), new PointF(box.X + 10.4f, box.Y + 4.8f) });
            }
            else using (var pen = new Pen(Palette.Muted, 1.5f)) g.DrawPath(pen, shape);
        }
        else
        {
            using var f = new Font(Fonts.Ui, 11, GraphicsUnit.Pixel);
            DrawText(g, "✕", f, Palette.Danger, box, StringAlignment.Center);
        }
        DrawText(g, label, font, check == null ? Palette.Danger : Palette.Text, new RectangleF(r.X + 31, r.Y, r.Width - 39, r.Height));
    }

    static void Sep(Graphics g, float y)
    {
        using var b = new SolidBrush(Palette.PanelLine);
        g.FillRectangle(b, 11, y, 192, 1);
    }

    static void DrawText(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment align = StringAlignment.Near)
    {
        using var b = new SolidBrush(c);
        using var fmt = new StringFormat(StringFormat.GenericTypographic) { Alignment = align, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(s, f, b, r, fmt);
    }

    // ---------- мышь и клавиши ----------
    PointF Logical(Point p) => new((float)(p.X / dpi) - ML, (float)(p.Y / dpi) - MT);

    string Hit(PointF p)
    {
        if (RectangleF.Inflate(Slider, 0, 4).Contains(p)) return "slider";
        for (int i = 0; i < PRESETS.Length; i++) if (Preset(i).Contains(p)) return "p" + i;
        for (int i = 0; i < DieRenderer.Styles.Length; i++) if (Swatch(i).Contains(p)) return "s" + i;
        if (ItemVerdict.Contains(p)) return "verdict";
        if (ItemTop.Contains(p)) return "top";
        if (ItemAuto.Contains(p)) return "auto";
        if (ItemClose.Contains(p)) return "close";
        return null;
    }

    void Slide(PointF p) => die.SetFreq((int)Math.Round(Math.Clamp((p.X - IN_X - 7) / (IN_W - 14), 0, 1) * 100));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = Logical(e.Location);
        if (sliding) { Slide(p); Draw(); return; }
        var h = Hit(p);
        if (h != hover) { hover = h; Cursor = h == null ? Cursors.Default : Cursors.Hand; Draw(); }
    }

    protected override void OnMouseLeave(EventArgs e) { hover = null; Draw(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var p = Logical(e.Location);
        if (!new RectangleF(0, 0, W, H).Contains(p)) { Close(); return; }   // клик по тени — мимо меню
        if (e.Button != MouseButtons.Left) return;
        pressed = Hit(p);
        if (pressed == "slider") { sliding = true; Slide(p); Draw(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var h = Hit(Logical(e.Location));
        bool click = !sliding && h != null && h == pressed;
        sliding = false; pressed = null;
        if (!click) return;
        if (h[0] == 'p' && h.Length == 2) die.SetFreq(PRESETS[h[1] - '0']);
        else if (h[0] == 's' && h.Length == 2) die.SetStyle(DieRenderer.Styles[h[1] - '0'].Id);
        else if (h == "verdict") die.ToggleVerdict();
        else if (h == "top") die.ToggleOnTop();
        else if (h == "auto")
        {
            if (die.Auto) die.StopAuto();
            else { Close(); die.StartAuto(); return; }   // включили — меню убираем, кубик сразу пошёл
        }
        else if (h == "close") { die.Close(); return; }
        Draw();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Close(); return; }
        int step = e.KeyCode is Keys.Right or Keys.Up ? 1 : e.KeyCode is Keys.Left or Keys.Down ? -1 : 0;
        if (step != 0) { die.SetFreq(die.Prefs.Freq + step); Draw(); }
    }
}
