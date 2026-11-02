// Окно кубика: прозрачное, без рамки — на экране только кубик. Пустые места пропускают клики насквозь.
// Клик — бросок, перетаскивание — подвинуть, колёсико — размер, правый клик — меню, PgUp — бросок из любого окна.
using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace PokerDice;

sealed unsafe class DieWindow : Form
{
    // окно постоянного размера (под самый крупный кубик), кубик — в его центре: масштаб меняет только картинку
    public const int WIN_W = 575, WIN_H = 625;
    const double RADIUS = 0.86;                     // половина видимого кубика (в радиусах описанного шара): столько держим от края экрана
    const double MIN_SCALE = 0.5, MAX_SCALE = 2.5;
    const double DURATION = 1400; const int TURNS = 2;
    const int HOTKEY_ID = 1;
    const int AUTO_PAUSE = 1600;                    // автобросок: столько показываем выпавшее, потом бросаем снова

    public readonly Prefs Prefs = Prefs.Load();
    readonly DieRenderer renderer = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    readonly System.Windows.Forms.Timer hotkeyRetry = new() { Interval = 5000 };
    readonly System.Windows.Forms.Timer autoPause = new() { Interval = AUTO_PAUSE };
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
    public enum Throw { Spin, Tumble, Top }         // кручение, кувырок, волчок
    struct Anim
    {
        public Throw Kind;
        public V3 Axis, Axis2;                      // главный поворот к выпавшей грани и второй — для кувырка
        public double Angle, Angle2, T0;
        public M3 End;
    }
    Anim? anim;
    Throw? lastThrow;
    MenuWindow menu;
    public long MenuClosedAt;                       // клик по кубику, закрывший меню, не бросает
    public bool Auto { get; private set; }          // автобросок: бросок за броском, пока не остановят
    public int Rolls { get; private set; }          // сколько бросков было (для проверки)

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

        renderer.SetStyle(Style);
        UseKind();

        var wa = Screen.PrimaryScreen.WorkingArea;
        center = Prefs.Cx is int cx && Prefs.Cy is int cy ? new Point(cx, cy) : new Point(wa.X + wa.Width / 2, wa.Y + wa.Height / 2);
        if (!Screen.AllScreens.Any(s => s.WorkingArea.Contains(center))) center = new Point(wa.X + wa.Width / 2, wa.Y + wa.Height / 2);
        Location = new Point(center.X - WIN_W / 2, center.Y - WIN_H / 2);   // окно сразу на нужном мониторе
        Size = new Size(WIN_W, WIN_H);
        timer.Tick += (s, e) => Tick();
        autoPause.Tick += (s, e) => { autoPause.Stop(); if (Auto) AutoRoll(); };
    }

    public DieStyle Style => DieRenderer.Styles.First(s => s.Id == Prefs.Style);
    public DieKind Kind { get; private set; }

    // кубик из настроек: числа на гранях и стартовое положение — лицом наибольшее число
    void UseKind()
    {
        Kind = DieKind.All.First(k => k.Id == Prefs.Die);
        renderer.SetKind(Kind);
        int face = 0;
        if (Kind.Random)
        {
            nums = new int[Kind.Faces];
            for (int i = 0; i < nums.Length; i++) nums[i] = Rng.Roll100();
            renderer.SetNumbers(nums);
        }
        else face = Array.IndexOf(Kind.Label, Kind.Sides);
        anim = null; hop = 0;
        orient = shown = DieRenderer.VIEW * Kind.ToFront[face];
    }

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
        if (m.Msg == Native.WM_HOTKEY) { RaiseQuietly(); RollOrStop(); return; }
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
        Present();
    }

    void Present() => buf.Present(Handle, center.X - buf.Width / 2, center.Y - buf.Height / 2);

    // кубик целиком остаётся на экране, куда его тащат
    void MoveCenter(Point p, bool redraw = false)
    {
        var wa = Screen.FromPoint(p).WorkingArea;
        int r = (int)Math.Ceiling(DieRenderer.SIZE * Kind.Bound * RADIUS * Prefs.Scale * Dpi);
        center = new Point(Math.Clamp(p.X, wa.Left + r, Math.Max(wa.Left + r, wa.Right - r)),
                           Math.Clamp(p.Y, wa.Top + r, Math.Max(wa.Top + r, wa.Bottom - r)));
        if (redraw) Frame();
        else Present();
    }

    // ---------- бросок ----------
    // каждый раз случайно, но не два одинаковых подряд
    public void Roll() => Roll(PickThrow());

    // клик, пробел, PgUp: при автоброске — остановить его (текущий бросок докатится и покажет число), иначе бросить
    void RollOrStop()
    {
        if (Auto) StopAuto();
        else Roll();
    }

    public void StartAuto()
    {
        Auto = true;
        AutoRoll();
    }

    public void StopAuto()
    {
        Auto = false;
        autoPause.Stop();
    }

    // пока открыто меню, не бросаем — бросок закрыл бы его; ждём ещё одну паузу
    void AutoRoll()
    {
        if (menu != null || anim != null) autoPause.Start();
        else Roll();
    }

    Throw PickThrow()
    {
        var all = Enum.GetValues<Throw>().Where(k => k != lastThrow).ToArray();
        return all[Rng.Next(all.Length)];
    }

    public void Roll(Throw kind)
    {
        if (anim != null) return;
        menu?.Close();
        int face;
        if (Kind.Random)                             // d100: выпавшее число — на случайной грани, на остальных любые
        {
            result = Rng.Roll100();
            face = Rng.Next(Kind.Faces);
            for (int i = 0; i < nums.Length; i++) nums[i] = i == face ? result.Value : Rng.Roll100();
            renderer.SetNumbers(nums);
        }
        else
        {
            result = Rng.Next(Kind.Sides) + 1;
            face = Array.IndexOf(Kind.Label, result.Value);
        }

        // из текущего положения в целевое вокруг одной оси (+ полные обороты — они не меняют, где кончим)
        var end = Kind.ToFront[face];
        var (axis, angle) = (DieRenderer.VIEW.T * orient * end.T).AxisAngle();
        double sign = Rng.Next(2) == 1 ? 1 : -1;
        var a = new Anim { Kind = kind, Axis = axis, Angle = angle, End = end, T0 = clock.Elapsed.TotalMilliseconds };
        switch (kind)
        {
            case Throw.Spin:
                a.Angle += 2 * Math.PI * TURNS * sign;
                break;
            case Throw.Tumble:                       // второй оборот — вокруг случайной оси поперёк первой
                a.Angle += 2 * Math.PI * sign;
                var across = V3.Cross(axis, new V3(0.3, 1, 0.2)).Norm();
                a.Axis2 = M3.RotAxis(axis, Rng.Next(360) * Math.PI / 180) * across;
                a.Angle2 = 2 * Math.PI * (Rng.Next(2) == 1 ? 1 : -1);
                break;
            case Throw.Top:
                a.Angle2 = 2 * Math.PI * 3 * sign;
                break;
        }
        anim = a;
        lastThrow = kind;
        Rolls++;
        timer.Start();
    }

    // положение кубика на доле t (0..1) броска: поворот и высота прыжка
    void Pose(Anim a, double t)
    {
        static double Out(double x, double p) => 1 - Math.Pow(1 - Math.Clamp(x, 0, 1), p);
        var view = DieRenderer.VIEW;
        switch (a.Kind)
        {
            case Throw.Spin:                         // кручение с подскоками
                shown = view * M3.RotAxis(a.Axis, a.Angle * (1 - Out(t, 3))) * a.End;
                hop = Math.Abs(Math.Sin(Math.PI * 2.5 * t)) * Math.Pow(1 - t, 1.3);
                break;
            case Throw.Tumble:                       // высокий подброс, кувырки через две оси, пара отскоков
                shown = view * M3.RotAxis(a.Axis, a.Angle * (1 - Out(t, 2.4))) * M3.RotAxis(a.Axis2, a.Angle2 * (1 - Out(t, 3.5))) * a.End;
                hop = t < 0.5 ? Math.Sin(2 * Math.PI * t) : 0.5 * Math.Abs(Math.Sin(4 * Math.PI * (t - 0.5))) * (1 - t);
                break;
            case Throw.Top:                          // волчок: крутится на углу вокруг вертикали и заваливается на грань
                shown = view * M3.RotY(a.Angle2 * (1 - Out(t, 2))) * M3.RotAxis(a.Axis, a.Angle * (1 - Out(t, 1.6))) * a.End;
                hop = t < 0.25 ? 0.4 * Math.Sin(4 * Math.PI * t) : t > 0.8 ? 0.1 * Math.Sin(5 * Math.PI * (t - 0.8)) : 0;
                break;
        }
        // крупный кубик не должен выпрыгивать из окна
        hop *= Math.Clamp((WIN_H / 2.0 / (DieRenderer.SIZE * scale) - 0.95) / 0.44, 0.3, 1);
    }

    void Tick()
    {
        double now = clock.Elapsed.TotalMilliseconds;
        if (anim is { } a)
        {
            double t = Math.Min(1, (now - a.T0) / DURATION);
            Pose(a, t);
            if (t >= 1) { orient = shown; hop = 0; anim = null; if (Auto) autoPause.Start(); }
        }
        // масштаб: колёсико задаёт цель, кубик плавно доезжает до неё
        if (scale != Prefs.Scale)
        {
            double diff = Prefs.Scale - scale;
            scale = Math.Abs(diff) < 0.003 ? Prefs.Scale : scale + diff * 0.22;
        }
        Frame();
        if (anim == null && scale == Prefs.Scale) timer.Stop();
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
    public void SetStyle(string id) { Prefs.Style = id; Prefs.Save(); renderer.SetStyle(Style); Frame(); }
    public void SetKind(string id)
    {
        Prefs.Die = id; Prefs.Save();
        UseKind();                                  // бросок, если шёл, обрывается — автобросок продолжаем с паузы
        if (Auto) autoPause.Start();
        MoveCenter(center, true);
    }
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
            if (!d.moved && !d.swallow) RollOrStop();
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
        if (e.KeyCode is Keys.Space or Keys.Enter) RollOrStop();
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

    // раскадровка броска: кадры по ходу анимации в одну ленту (в половину размера)
    public Bitmap Filmstrip(Throw kind, int frames)
    {
        Roll(kind);
        var a = anim.Value;
        int fw = buf.Width / 2, fh = buf.Height / 2;
        var strip = new Bitmap(fw * frames, fh);
        using (var g = Graphics.FromImage(strip))
            for (int i = 0; i < frames; i++)
            {
                Pose(a, (i + 0.5) / frames);
                Frame();
                g.DrawImage(bufBmp, i * fw, 0, fw, fh);
            }
        Pose(a, 1);
        orient = shown; hop = 0; anim = null;
        return strip;
    }

    // кубик крупным планом на масштабе s с одними и теми же числами: проверить, как читаются цифры
    public Bitmap Closeup(double s)
    {
        if (Kind.Random && nums[0] != 39) { nums = new[] { 39, 48, 61, 86, 14, 100 }; renderer.SetNumbers(nums); }
        scale = s; hop = 0;
        Frame();
        int r = Math.Min((int)Math.Ceiling(DieRenderer.SIZE * Kind.Bound * (Kind.Cube ? RADIUS : 1.05) * s * Dpi) + 4, buf.Height / 2);
        return bufBmp.Clone(new Rectangle(buf.Width / 2 - r, buf.Height / 2 - r, 2 * r, 2 * r), bufBmp.PixelFormat);
    }

    public Bitmap Snapshot(bool rolled)
    {
        if (rolled) { Roll(); while (anim != null) { Tick(); Thread.Sleep(15); } }
        else Frame();
        return new Bitmap(bufBmp);
    }
}
