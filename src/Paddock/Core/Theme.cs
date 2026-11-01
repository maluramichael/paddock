using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Paddock.Interop;

namespace Paddock.Core;

/// <summary>Ein Theme als JSON-Datei. Alle Felder sind optional – fehlende Werte kommen vom Standard.</summary>
public sealed class Theme
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Theme";
    public string? Description { get; set; }
    public bool Dark { get; set; } = true;
    public BackdropKind Backdrop { get; set; } = BackdropKind.Acrylic;
    public CornerKind Corners { get; set; } = CornerKind.Round;
    public string Tint { get; set; } = "#161A24";
    public double TintOpacity { get; set; } = 0.45;
    public string Border { get; set; } = "#26FFFFFF";
    public string Accent { get; set; } = "#7C8CFF";
    public string FontFamily { get; set; } = "Segoe UI Variable Text, Segoe UI";
    public TitleStyle Title { get; set; } = new();
    public ItemStyle Item { get; set; } = new();
    public MenuStyle Menu { get; set; } = new();
    /// <summary>Optional: Pfad (relativ zur Theme-Datei) zu einem XAML-ResourceDictionary für tiefere Anpassungen.</summary>
    public string? Resources { get; set; }

    public sealed class TitleStyle
    {
        public string FontFamily { get; set; } = "Segoe UI Variable Display, Segoe UI";
        public double FontSize { get; set; } = 13;
        public string FontWeight { get; set; } = "SemiBold";
        public string Foreground { get; set; } = "#F3F5FA";
        public string Background { get; set; } = "#0FFFFFFF";
        public double Height { get; set; } = 34;
        public string Alignment { get; set; } = "left";
        public bool Uppercase { get; set; }
    }

    public sealed class ItemStyle
    {
        public double FontSize { get; set; } = 12;
        public string Foreground { get; set; } = "#EDEFF5";
        public string Hover { get; set; } = "#17FFFFFF";
        public string Selected { get; set; } = "#337C8CFF";
        public string SelectedBorder { get; set; } = "#997C8CFF";
        public double Spacing { get; set; } = 4;
    }

    public sealed class MenuStyle
    {
        public string Background { get; set; } = "#F01C2029";
        public string Foreground { get; set; } = "#F3F5FA";
        public string Muted { get; set; } = "#9AA3B5";
        public string Hover { get; set; } = "#1FFFFFFF";
        public string Border { get; set; } = "#2EFFFFFF";
    }

    [System.Text.Json.Serialization.JsonIgnore] public string? SourceFile { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool BuiltIn { get; set; }
}

/// <summary>
/// Lädt eingebaute und eigene Themes (%APPDATA%\Paddock\themes\*.json), legt die Werte als
/// dynamische Ressourcen ab und lädt bei Dateiänderungen live neu.
/// </summary>
public sealed class ThemeManager
{
    public static ThemeManager Instance { get; } = new();

    readonly Dictionary<string, Theme> _themes = new(StringComparer.OrdinalIgnoreCase);
    FileSystemWatcher? _watcher;
    DispatcherTimer? _reload;
    ResourceDictionary? _extraResources;

    public Theme Current { get; private set; } = new() { Id = "standard" };
    public IReadOnlyCollection<Theme> Themes => _themes.Values;
    public event Action? Changed;

    public void Initialize(string themeId)
    {
        LoadAll();
        Apply(themeId);
        WatchUserThemes();
    }

    void LoadAll()
    {
        _themes.Clear();
        var asm = Assembly.GetExecutingAssembly();
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.EndsWith(".json") && n.Contains(".Themes.")))
        {
            using var s = asm.GetManifestResourceStream(res)!;
            var id = res.Split('.')[^2];
            if (TryParse(new StreamReader(s).ReadToEnd(), id, null) is { } t) { t.BuiltIn = true; _themes[id] = t; }
        }
        foreach (var file in Directory.EnumerateFiles(AppPaths.Themes, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            try { if (TryParse(File.ReadAllText(file), id, file) is { } t) _themes[id] = t; }
            catch (Exception ex) { Log.Error($"Theme {file}", ex); }
        }
    }

    static Theme? TryParse(string json, string id, string? file)
    {
        try
        {
            var t = JsonSerializer.Deserialize<Theme>(json, SettingsStore.Json);
            if (t is null) return null;
            t.Id = id;
            t.SourceFile = file;
            return t;
        }
        catch (Exception ex) { Log.Error($"Theme '{id}' ungültig", ex); return null; }
    }

    public void Apply(string themeId)
    {
        Current = _themes.TryGetValue(themeId, out var t) ? t : _themes.Values.FirstOrDefault() ?? new Theme();
        var r = Application.Current.Resources;
        var th = Current;

        r["P.TintColor"] = ParseColor(th.Tint, Colors.Black);
        r["P.Tint"] = Freeze(new SolidColorBrush(WithOpacity(ParseColor(th.Tint, Colors.Black), th.TintOpacity)));
        r["P.Border"] = Brush(th.Border);
        r["P.AccentColor"] = ParseColor(th.Accent, Colors.SteelBlue);
        r["P.Accent"] = Brush(th.Accent);
        // Text/Knöpfe auf Akzentflächen: hell oder dunkel je nach Helligkeit des Akzents (z. B. weißer Akzent bei „Klar“)
        r["P.AccentForeground"] = Freeze(new SolidColorBrush(Luminance(ParseColor(th.Accent, Colors.SteelBlue)) > 0.6 ? Color.FromRgb(0x14, 0x16, 0x1C) : Colors.White));
        r["P.AccentSoft"] = Freeze(new SolidColorBrush(WithOpacity(ParseColor(th.Accent, Colors.SteelBlue), 0.22)));
        r["P.FontFamily"] = new FontFamily(th.FontFamily);
        r["P.CornerRadius"] = new CornerRadius(th.Corners switch { CornerKind.Round => 8, CornerKind.Small => 4, _ => 0 });

        r["P.Title.FontFamily"] = new FontFamily(th.Title.FontFamily);
        r["P.Title.FontSize"] = th.Title.FontSize;
        r["P.Title.FontWeight"] = ParseWeight(th.Title.FontWeight);
        r["P.Title.Foreground"] = Brush(th.Title.Foreground);
        r["P.Title.Background"] = Brush(th.Title.Background);
        r["P.Title.Height"] = th.Title.Height;
        r["P.Title.Alignment"] = th.Title.Alignment.ToLowerInvariant() switch
        {
            "center" => HorizontalAlignment.Center,
            "right" => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left,
        };

        r["P.Item.FontSize"] = th.Item.FontSize;
        r["P.Item.Foreground"] = Brush(th.Item.Foreground);
        r["P.Item.Hover"] = Brush(th.Item.Hover);
        r["P.Item.Selected"] = Brush(th.Item.Selected);
        r["P.Item.SelectedBorder"] = Brush(th.Item.SelectedBorder);
        r["P.Item.Spacing"] = new Thickness(th.Item.Spacing / 2);

        r["P.Menu.Background"] = Brush(th.Menu.Background);
        r["P.Menu.Foreground"] = Brush(th.Menu.Foreground);
        r["P.Menu.Muted"] = Brush(th.Menu.Muted);
        r["P.Menu.Hover"] = Brush(th.Menu.Hover);
        r["P.Menu.Border"] = Brush(th.Menu.Border);

        // App-Oberfläche (Einstellungen, Dialoge) folgt hell/dunkel des Themes
        bool d = th.Dark;
        r["P.Ui.Background"] = Brush(d ? "#E6181B22" : "#F2F6F7FA");
        r["P.Ui.Solid"] = Brush(d ? "#1B1E26" : "#F6F7FA");
        r["P.Ui.Foreground"] = Brush(d ? "#F3F5FA" : "#16181D");
        r["P.Ui.Muted"] = Brush(d ? "#9AA3B5" : "#5B6272");
        r["P.Ui.Card"] = Brush(d ? "#0DFFFFFF" : "#B3FFFFFF");
        r["P.Ui.CardBorder"] = Brush(d ? "#14FFFFFF" : "#14000000");
        r["P.Ui.Control"] = Brush(d ? "#14FFFFFF" : "#0D000000");
        r["P.Ui.ControlHover"] = Brush(d ? "#21FFFFFF" : "#17000000");
        r["P.Ui.Dark"] = d;

        ApplyExtraResources(th);
        try { NativeMethods.SetPreferredAppMode(d ? 2 : 3); NativeMethods.FlushMenuThemes(); } catch { }
        Changed?.Invoke();
    }

    void ApplyExtraResources(Theme th)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_extraResources is not null) merged.Remove(_extraResources);
        _extraResources = null;
        if (th.Resources is null || th.SourceFile is null) return;
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(th.SourceFile)!, th.Resources);
            using var fs = File.OpenRead(path);
            _extraResources = (ResourceDictionary)XamlReader.Load(fs);
            merged.Add(_extraResources);
        }
        catch (Exception ex) { Log.Error($"Theme-Ressourcen '{th.Resources}'", ex); }
    }

    void WatchUserThemes()
    {
        _reload = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _reload.Tick += (_, _) => { _reload.Stop(); LoadAll(); Apply(Current.Id); };
        _watcher = new FileSystemWatcher(AppPaths.Themes) { IncludeSubdirectories = false, EnableRaisingEvents = true };
        FileSystemEventHandler h = (_, _) => Application.Current.Dispatcher.BeginInvoke(() => { _reload.Stop(); _reload.Start(); });
        _watcher.Changed += h; _watcher.Created += h; _watcher.Deleted += h;
        _watcher.Renamed += (s, e) => h(s, e);
    }

    /// <summary>Kopiert ein eingebautes Theme als Vorlage in den Benutzerordner.</summary>
    public string ExportTemplate(string themeId)
    {
        var asm = Assembly.GetExecutingAssembly();
        var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith($".Themes.{themeId}.json", StringComparison.OrdinalIgnoreCase))
                  ?? asm.GetManifestResourceNames().First(n => n.Contains(".Themes."));
        var target = Path.Combine(AppPaths.Themes, $"mein-{themeId}.json");
        int i = 2;
        while (File.Exists(target)) target = Path.Combine(AppPaths.Themes, $"mein-{themeId}-{i++}.json");
        using var s = asm.GetManifestResourceStream(res)!;
        var json = new StreamReader(s).ReadToEnd();
        var name = _themes.TryGetValue(themeId, out var t) ? t.Name : themeId;
        File.WriteAllText(target, json.Replace($"\"name\": \"{name}\"", $"\"name\": \"{name} (eigen)\""));
        return target;
    }

    public static Color ParseColor(string? s, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(s.Trim()); } catch { return fallback; }
    }

    /// <summary>Relative Helligkeit 0..1 (sRGB, vereinfacht).</summary>
    public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    public static Color WithOpacity(Color c, double opacity) => Color.FromArgb((byte)Math.Clamp(opacity * 255, 0, 255), c.R, c.G, c.B);

    static Brush Brush(string s) => Freeze(new SolidColorBrush(ParseColor(s, Colors.Transparent)));
    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    static FontWeight ParseWeight(string s)
    {
        try { return (FontWeight)new FontWeightConverter().ConvertFromString(s)!; } catch { return FontWeights.SemiBold; }
    }
}
