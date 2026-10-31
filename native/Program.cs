// Кубик: рандомайзер 1–100 для смешанных стратегий. Одна копия на компьютер.
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

    static void Shot(string dir)
    {
        ApplicationConfiguration.Initialize();
        Prefs.ReadOnly = true;
        Directory.CreateDirectory(dir);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var die = new DieWindow();
        var handle = die.Handle;                     // окно создано, но не показано
        Console.WriteLine($"start {sw.ElapsedMilliseconds} ms");
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
        }
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
                var panel = new Rectangle(m.Location.X + (int)(18 * die.Dpi), m.Location.Y + (int)(12 * die.Dpi), (int)(214 * die.Dpi), (int)(319 * die.Dpi));
                Console.WriteLine($"menu at {at}: {(wa.Contains(panel) ? "OK" : "FAIL")} {panel}");
            }
        }
        using var menu = new MenuWindow(die, new Point(100, 100));
        menu.Snapshot().Save(Path.Combine(dir, "menu.png"));
        Console.WriteLine("ok");
    }
}
