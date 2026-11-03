// Кубик: рандомайзер 1–100 для смешанных стратегий и кубики d4–d20 для настолок. Одна копия на компьютер.
namespace PokerDice;

static class Program
{
    public static readonly int ShowMsg = Native.RegisterWindowMessage("PokerDice.Show");

    [STAThread]
    static void Main(string[] args)
    {
        // для проверки: --shot <папка> — сохранить картинки кубика и меню и выйти
        if (args.Length == 2 && args[0] == "--shot") { Shot(args[1]); return; }

        using var single = new Mutex(true, "PokerDice.Single", out bool first);
        if (!first)
        {
            Native.PostMessage(Native.HWND_BROADCAST, ShowMsg, IntPtr.Zero, IntPtr.Zero);   // показать уже запущенный
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new DieWindow());
    }

    static void Pump(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(5); }
    }

    static void Shot(string dir)
    {
        ApplicationConfiguration.Initialize();
        Prefs.ReadOnly = true;
        Directory.CreateDirectory(dir);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var die = new DieWindow();
        var handle = die.Handle;                     // окно создано, но не показано
        Console.WriteLine($"start {sw.ElapsedMilliseconds} ms");
        // смена раскладки на русскую (код 1049): раньше роняла кубик с ошибкой про культуру
        Native.SendMessage(handle, Native.WM_INPUTLANGCHANGE, (IntPtr)204, (IntPtr)0x04190419);
        Console.WriteLine("lang change OK");
        die.Snapshot(false).Save(Path.Combine(dir, "idle.png"));
        sw.Restart();
        var cpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
        die.Snapshot(true).Save(Path.Combine(dir, "rolled.png"));
        Console.WriteLine($"roll {sw.ElapsedMilliseconds} ms, cpu {(System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds:F0} ms");
        foreach (var s in new[] { 0.5, 1, 1.6, 2.5 }) Console.WriteLine($"frame at {s * 100}%: {die.Bench(s, 20):F1} ms");
        die.Snapshot(false).Save(Path.Combine(dir, "big.png"));   // последний замер — на 250%
        foreach (var st in DieRenderer.Styles)
        {
            die.SetStyle(st.Id);
            die.Snapshot(false).Save(Path.Combine(dir, st.Id + ".png"));
            foreach (var s in new[] { 0.5, 0.7, 1.0, 2.5 }) die.Closeup(s).Save(Path.Combine(dir, $"{st.Id}-{s * 100}.png"));
        }
        foreach (var s in new[] { 1.0, 2.5 })
        {
            die.Closeup(s);
            foreach (var kind in Enum.GetValues<DieWindow.Throw>())
                die.Filmstrip(kind, 8).Save(Path.Combine(dir, $"throw-{kind}-{s * 100}.png"));
        }
        // все кубики: грань с выпавшим числом встаёт лицом к зрителю, числа — ровно 1..n, картинки и скорость
        foreach (var k in DieKind.All)
        {
            die.SetKind(k.Id);
            bool faces = k.ToFront.Select((m, i) => (m * k.N[i] - new V3(0, 0, 1)).Length).Max() < 1e-9;
            // выпавшая грань смотрит на зрителя прямее всех остальных
            var view = DieRenderer.ViewOf(k);
            bool front = Enumerable.Range(0, k.Faces).All(f => Enumerable.Range(0, k.Faces)
                .All(o => o == f || (view * k.ToFront[f] * k.N[o]).Z < (view * k.ToFront[f] * k.N[f]).Z - 0.05));
            bool labels = k.Random || k.Label.OrderBy(x => x).SequenceEqual(Enumerable.Range(1, k.Sides));
            Console.WriteLine($"{k.Id}: faces {(faces ? "OK" : "FAIL")}, result in front {(front ? "OK" : "FAIL")}, numbers {(labels ? "OK" : "FAIL")}, frame at 100% {die.Bench(1, 10):F1} ms, at 250% {die.Bench(2.5, 10):F1} ms");
            die.Snapshot(true).Save(Path.Combine(dir, $"kind-{k.Id}.png"));
            die.Closeup(1).Save(Path.Combine(dir, $"kind-{k.Id}-100.png"));
            die.Closeup(2.5).Save(Path.Combine(dir, $"kind-{k.Id}-250.png"));
            die.Filmstrip(DieWindow.Throw.Tumble, 8).Save(Path.Combine(dir, $"kind-{k.Id}-throw.png"));
        }
        die.SetKind(DieKind.Default.Id);
        for (int i = 0; i < 2; i++)
        {
            sw.Restart();
            using var mm = new MenuWindow(die, new Point(300, 300));
            var hh = mm.Handle;
            Console.WriteLine($"menu open {sw.ElapsedMilliseconds} ms");
        }
        foreach (var scr in Screen.AllScreens)       // меню у всех углов всех экранов — целиком на экране
        {
            var wa = scr.WorkingArea;
            foreach (var at in new[] { new Point(wa.Left + 5, wa.Top + 5), new Point(wa.Right - 5, wa.Top + 5), new Point(wa.Left + 5, wa.Bottom - 5), new Point(wa.Right - 5, wa.Bottom - 5) })
            {
                using var m = new MenuWindow(die, at);
                var panel = new Rectangle(m.Location.X + (int)(18 * die.Dpi), m.Location.Y + (int)(12 * die.Dpi), (int)(MenuWindow.W * die.Dpi), (int)(MenuWindow.H * die.Dpi));
                Console.WriteLine($"menu at {at}: {(wa.Contains(panel) ? "OK" : "FAIL")} {panel}");
            }
        }
        // автобросок: за 6 с — бросок за броском, после остановки новых нет
        int before = die.Rolls;
        die.StartAuto();
        Pump(6000);
        int auto = die.Rolls - before;
        die.StopAuto();
        Pump(500);
        before = die.Rolls;
        Pump(4000);
        Console.WriteLine($"auto: {auto} rolls in 6 s, after stop: {die.Rolls - before} {(auto >= 2 && die.Rolls == before ? "OK" : "FAIL")}");
        using var menu = new MenuWindow(die, new Point(100, 100));
        menu.Snapshot().Save(Path.Combine(dir, "menu.png"));
        Console.WriteLine("ok");
    }
}
