// Окно кубика: прозрачное, без рамки — на экране только кубик. Пустые места пропускают клики насквозь.
// Клик — бросок, перетаскивание — подвинуть, колёсико — размер, правый клик — меню, PgUp — бросок из любого окна.
using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace PokerDice;

sealed unsafe class DieWindow : Form
{
    // окно постоянного размера (под самый крупный кубик), кубик — в его центре: масштаб меняет только картинку
    public const int WIN_W = 575, WIN_H = 625;
    const double RADIUS = 75;                       // половина видимого кубика при 100%: столько держим от края экрана
    const double MIN_SCALE = 0.5, MAX_SCALE = 2.5;
    const double DURATION = 1400; const int TURNS = 2;
    const int HOTKEY_ID = 1;

    public readonly Prefs Prefs = Prefs.Load();
    readonly DieRenderer renderer = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    readonly System.Windows.Forms.Timer hotkeyRetry = new() { Interval = 5000 };
    readonly Stopwatch clock = Stopwatch.StartNew();
    LayeredBuffer buf;
    Bitmap bufBmp;
    public double Dpi { get; private set; } = 1;
    Point center;                                   // центр кубика на экране (физические пиксели)
    double scale;
    int[] nums = new int[6];
    int? result;
    M3 orient = DieRenderer.VIEW, shown = DieRenderer.VIEW;
    double hop;
    (V3 axis, double angle, M3 end, double t0)? anim;
    double popT0 = -1;                              // когда начала «выпрыгивать» подсказка
    bool verdictUp;                                 // подсказка над кубиком (снизу нет места до края экрана)
    MenuWindow menu;
    public long MenuClosedAt;                       // клик по кубику, закрывший меню, не бросает

    public DieWindow()
    {
        Text = "Кубик";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = true;
        Cursor = Cursors.Hand;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        TopMost = Prefs.OnTop;
        scale = Prefs.Scale;

        for (int i = 0; i < 6; i++) nums[i] = Rng.Roll100();
        renderer.SetStyle(Style);
        renderer.SetNumbers(nums);

        var wa = Screen.PrimaryScreen.WorkingArea;
        center = Prefs.Cx is int cx && Prefs.Cy is int cy ? new Point(cx, cy) : new Point(wa.X + wa.Width / 2, wa.Y + wa.Height / 2);
        if (!Screen.AllScreens.Any(s => s.WorkingArea.Contains(center))) center = new Point(wa.X + wa.Width / 2, wa.Y + wa.Height / 2);
        Location = new Point(center.X - WIN_W / 2, center.Y - WIN_H / 2);   // окно сразу на нужном мониторе
        Size = new Size(WIN_W, WIN_H);
        timer.Tick += (s, e) => Tick();
    }

    public DieStyle Style => DieRenderer.Styles.First(s => s.Id == Prefs.Style);

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= Native.WS_EX_LAYERED; return cp; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetDpi(DeviceDpi / 96.0);
        // PgUp может быть занят другой программой (например, старым кубиком) — пробуем забрать раз в 5 секунд
        if (!Native.RegisterHotKey(Handle, HOTKEY_ID, Native.MOD_NOREPEAT, Native.VK_PRIOR))
        {
            hotkeyRetry.Tick += (s, ev) => { if (Native.RegisterHotKey(Handle, HOTKEY_ID, Native.MOD_NOREPEAT, Native.VK_PRIOR)) hotkeyRetry.Stop(); };
            hotkeyRetry.Start();
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        e.Cancel = true;                            // размер окна держим сами
        SetDpi(e.DeviceDpiNew / 96.0);
    }

    void SetDpi(double d)
    {
        Dpi = d;
        bufBmp?.Dispose(); buf?.Dispose();
        buf = new LayeredBuffer((int)Math.Round(WIN_W * d), (int)Math.Round(WIN_H * d));
        bufBmp = buf.AsBitmap();
        MoveCenter(center, true);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Native.UnregisterHotKey(Handle, HOTKEY_ID);
        Prefs.Cx = center.X; Prefs.Cy = center.Y;
        Prefs.Save();
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY) { RaiseQuietly(); Roll(); return; }
        if (m.Msg == Program.ShowMsg) { RaiseQuietly(); Activate(); return; }
        base.WndProc(ref m);
    }

    // кубик всплывает над остальными окнами, но фокус у игры не забирает
    void RaiseQuietly()
    {
        if (TopMost) return;
        const uint f = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, f);
        Native.SetWindowPos(Handle, Native.HWND_NOTOPMOST, 0, 0, 0, 0, f);
    }

    // ---------- кадр ----------
    void Frame()
    {
        buf.Pixels.Clear();
        renderer.Render((uint*)buf.Bits, buf.Width, buf.Height, DieRenderer.SIZE * scale * Dpi, shown, hop);
        DrawVerdict();
        Present();
    }

    void Present() => buf.Present(Handle, center.X - buf.Width / 2, center.Y - buf.Height / 2);

    // кубик целиком остаётся на экране, куда его тащат
    void MoveCenter(Point p, bool redraw = false)
    {
        var wa = Screen.FromPoint(p).WorkingArea;
        int r = (int)Math.Ceiling(RADIUS * Prefs.Scale * Dpi);
        center = new Point(Math.Clamp(p.X, wa.Left + r, Math.Max(wa.Left + r, wa.Right - r)),
                           Math.Clamp(p.Y, wa.Top + r, Math.Max(wa.Top + r, wa.Bottom - r)));
        bool up = center.Y + (124 * scale + 4) * Dpi > wa.Bottom;
        if (redraw || (up != verdictUp && VerdictVisible)) { verdictUp = up; Frame(); }
        else { verdictUp = up; Present(); }
    }

    // ---------- подсказка рейз/фолд под кубиком (растёт вместе с ним) ----------
    bool VerdictVisible => result != null && anim == null && Prefs.Verdict;

    void DrawVerdict()
    {
        if (!VerdictVisible) return;
        bool raise = result <= Prefs.Freq;
        string text = raise ? "РЕЙЗ" : "ФОЛД";
        float s = (float)(scale * Dpi), fs = (float)(Math.Max(10, 12 * scale) * Dpi);
        float pop = 1;
        if (popT0 >= 0) pop = (float)CubicBezier(0.2, 1.6, 0.4, 1, Math.Min(1, (clock.Elapsed.TotalMilliseconds - popT0) / 300));

        using var g = Graphics.FromImage(bufBmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = new GraphicsPath();
        using var font = new Font(Fonts.Ui, fs, FontStyle.Bold, GraphicsUnit.Pixel);
        float x = 0;
        foreach (var ch in text)                   // разрядка .2em
        {
            path.AddString(ch.ToString(), Fonts.Ui, (int)FontStyle.Bold, fs, new PointF(x, 0), StringFormat.GenericTypographic);
            x += g.MeasureString(ch.ToString(), font, PointF.Empty, StringFormat.GenericTypographic).Width + 0.2f * fs;
        }
        float w = 14 * s + x + 12 * s, h = fs + 14 * s;
        float cx = buf.Width / 2f, off = 124 * s;
        float top = verdictUp ? buf.Height / 2f - off : buf.Height / 2f + off - h;
        var pill = new RectangleF(cx - w / 2, top, w, h);
        var tb = path.GetBounds();
        using (var mt = new Matrix())
        {
            mt.Translate(pill.X + 14 * s - tb.X + (x - 0.2f * fs - tb.Width) / 2, pill.Y + h / 2 - (tb.Y + tb.Height / 2));
            path.Transform(mt);
        }

        g.TranslateTransform(cx, top + h / 2);    // «выпрыгивание» из центра плашки
        g.ScaleTransform(0.6f + 0.4f * pop, 0.6f + 0.4f * pop);
        g.TranslateTransform(-cx, -(top + h / 2));
        int A(double a) => (int)Math.Round(Math.Clamp(a * Math.Clamp(pop, 0, 1), 0, 1) * 255);

        for (int i = 4; i >= 1; i--)               // мягкая тень 0 2px 8px
        {
            var r = RectangleF.Inflate(pill, i * 1.5f * (float)Dpi, i * 1.5f * (float)Dpi);
            r.Offset(0, 2 * (float)Dpi);
            using var sp = Shapes.Round(r, r.Height / 2);
            using var sb = new SolidBrush(Color.FromArgb(A(0.035), 0, 0, 0));
            g.FillPath(sb, sp);
        }
        using (var ring = Shapes.Round(RectangleF.Inflate(pill, (float)Dpi, (float)Dpi), h / 2 + (float)Dpi))
        using (var rb = new SolidBrush(Color.FromArgb(A(0.1), 0, 0, 0)))
            g.FillPath(rb, ring);
        var bg = raise ? Palette.Gold : Palette.Die;
        using (var pp = Shapes.Round(pill, h / 2))
        using (var bb = new SolidBrush(Color.FromArgb(A(1), bg)))
            g.FillPath(bb, pp);
        using (var tbr = new SolidBrush(Color.FromArgb(A(1), Palette.Ink)))
            g.FillPath(tbr, path);
    }

    static double CubicBezier(double x1, double y1, double x2, double y2, double x)
    {
        static double B(double a, double b, double t) => 3 * a * (1 - t) * (1 - t) * t + 3 * b * (1 - t) * t * t + t * t * t;
        static double D(double a, double b, double t) => 3 * a * (1 - t) * (1 - t) + 6 * (b - a) * (1 - t) * t + 3 * (1 - b) * t * t;
        double t = x;
        for (int i = 0; i < 8; i++)
        {
            double d = D(x1, x2, t);
            if (Math.Abs(d) < 1e-6) break;
            t = Math.Clamp(t - (B(x1, x2, t) - x) / d, 0, 1);
        }
        return B(y1, y2, t);
    }

    // ---------- бросок ----------
    public void Roll()
    {
        if (anim != null) return;
        menu?.Close();
        result = Rng.Roll100();
        int face = Rng.Next(6);
        for (int i = 0; i < 6; i++) nums[i] = i == face ? result.Value : Rng.Roll100();
        renderer.SetNumbers(nums);

        // из текущего положения в целевое вокруг одной оси + пара полных оборотов
        var end = DieRenderer.TO_FRONT[face];
        var (axis, angle) = (DieRenderer.VIEW.T * orient * end.T).AxisAngle();
        angle += 2 * Math.PI * TURNS * (Rng.Next(2) == 1 ? 1 : -1);
        anim = (axis, angle, end, clock.Elapsed.TotalMilliseconds);
        popT0 = -1;
        timer.Start();
    }

    void Tick()
    {
        double now = clock.Elapsed.TotalMilliseconds;
        bool popDone = false;
        if (anim is { } a)
        {
            double t = Math.Min(1, (now - a.t0) / DURATION), e = 1 - Math.Pow(1 - t, 3);
            shown = DieRenderer.VIEW * M3.RotAxis(a.axis, a.angle * (1 - e)) * a.end;
            hop = Math.Abs(Math.Sin(Math.PI * 2.5 * t)) * Math.Pow(1 - t, 1.3);
            if (t >= 1) { orient = shown; hop = 0; anim = null; popT0 = now; }
        }
        // масштаб: колёсико задаёт цель, кубик плавно доезжает до неё
        if (scale != Prefs.Scale)
        {
            double diff = Prefs.Scale - scale;
            scale = Math.Abs(diff) < 0.003 ? Prefs.Scale : scale + diff * 0.22;
            var wa = Screen.FromPoint(center).WorkingArea;
            verdictUp = center.Y + (124 * scale + 4) * Dpi > wa.Bottom;
        }
        if (popT0 >= 0) popDone = now - popT0 >= 300;
        Frame();
        if (popDone) popT0 = -1;
        if (anim == null && scale == Prefs.Scale && popT0 < 0) timer.Stop();
    }

    public void SetScale(double k)
    {
        k = Math.Round(Math.Clamp(k, MIN_SCALE, MAX_SCALE), 1);
        if (k == Prefs.Scale) return;
        Prefs.Scale = k;
        Prefs.Save();
        MoveCenter(center);                         // подросший у края кубик отодвинуть внутрь экрана
        timer.Start();
    }

    // ---------- из меню ----------
    public void SetFreq(int v) { Prefs.Freq = Math.Clamp(v, 0, 100); Prefs.Save(); if (VerdictVisible) Frame(); }
    public void SetStyle(string id) { Prefs.Style = id; Prefs.Save(); renderer.SetStyle(Style); Frame(); }
    public void ToggleVerdict() { Prefs.Verdict = !Prefs.Verdict; Prefs.Save(); Frame(); }
    public void ToggleOnTop() { Prefs.OnTop = !Prefs.OnTop; Prefs.Save(); TopMost = Prefs.OnTop; }

    void OpenMenu(Point at)
    {
        menu?.Close();
        menu = new MenuWindow(this, at);
        menu.FormClosed += (s, e) => { menu = null; MenuClosedAt = Environment.TickCount64; };
        menu.Show(this);
    }

    // ---------- мышь и клавиши ----------
    (Point mouse, Point center, bool moved, bool swallow)? drag;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            drag = (Cursor.Position, center, false, Environment.TickCount64 - MenuClosedAt < 300);
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (drag is { } d)
        {
            var p = Cursor.Position;
            int dx = p.X - d.mouse.X, dy = p.Y - d.mouse.Y;
            if (!d.moved && Math.Sqrt(dx * dx + dy * dy) < 4 * Dpi) return;
            drag = d with { moved = true };
            MoveCenter(new Point(d.center.X + dx, d.center.Y + dy));
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && drag is { } d)
        {
            drag = null;
            if (!d.moved && !d.swallow) Roll();
        }
        if (e.Button == MouseButtons.Right) OpenMenu(Cursor.Position);
        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        SetScale(Prefs.Scale + (e.Delta > 0 ? 0.1 : -0.1));
        base.OnMouseWheel(e);
    }

    long lastEsc;
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) Roll();
        if (e.KeyCode == Keys.Escape)
        {
            if (Environment.TickCount64 - lastEsc < 600) Close();
            lastEsc = Environment.TickCount64;
        }
        base.OnKeyDown(e);
    }

    // ---------- для проверки (Program --shot) ----------
    public double Bench(double s, int frames)
    {
        scale = s;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++) { hop = i % 2 * 0.5; Frame(); }
        double ms = sw.Elapsed.TotalMilliseconds / frames;
        hop = 0;
        return ms;
    }

    public Bitmap Snapshot(bool rolled)
    {
        if (rolled) { Prefs.Verdict = true; Roll(); while (anim != null || popT0 >= 0) { Tick(); Thread.Sleep(15); } }
        else Frame();
        return new Bitmap(bufBmp);
    }
}
