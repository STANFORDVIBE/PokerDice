// Настройки и место кубика: %APPDATA%\PokerDice\prefs.json
using System.Text.Json;

namespace PokerDice;

sealed class Prefs
{
    public int Freq { get; set; } = 50;
    public double Scale { get; set; } = 1;
    public string Style { get; set; } = "classic";
    public bool Verdict { get; set; } = true;
    public bool OnTop { get; set; }
    public int? Cx { get; set; }              // центр кубика на экране
    public int? Cy { get; set; }

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PokerDice");
    static string File => Path.Combine(Dir, "prefs.json");

    public static Prefs Load()
    {
        Prefs p;
        try { p = JsonSerializer.Deserialize<Prefs>(System.IO.File.ReadAllText(File)) ?? new(); }
        catch { p = new(); }
        if (p.Cx == null) try
        {
            // место из старой версии на Electron
            var old = Path.Combine(Path.GetDirectoryName(Dir)!, "poker-dice", "position.json");
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(old));
            p.Cx = (int)doc.RootElement.GetProperty("cx").GetDouble();
            p.Cy = (int)doc.RootElement.GetProperty("cy").GetDouble();
        }
        catch { }
        p.Freq = Math.Clamp(p.Freq, 0, 100);
        p.Scale = Math.Round(Math.Clamp(p.Scale, 0.5, 2.5), 1);
        if (DieRenderer.Styles.All(s => s.Id != p.Style)) p.Style = "classic";
        return p;
    }

    public static bool ReadOnly;              // режим проверки (--shot) не трогает настоящие настройки

    public void Save()
    {
        if (ReadOnly) return;
        try
        {
            Directory.CreateDirectory(Dir);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}
