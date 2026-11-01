using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace Paddock.Core;

public static class AppPaths
{
    /// <summary>
    /// Datenordner. Reihenfolge: Umgebungsvariable PADDOCK_DATA → portabler Modus (Ordner "data" neben der Exe)
    /// → %APPDATA%\Paddock.
    /// </summary>
    public static string Roaming { get; } = Ensure(PortableRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Paddock"));
    public static string Local { get; } = Ensure(PortableRoot is { } p ? Path.Combine(p, "local") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Paddock"));
    public static bool IsPortable => PortableRoot is not null;

    static string? PortableRoot
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("PADDOCK_DATA");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            var local = Path.Combine(AppContext.BaseDirectory, "data");
            return Directory.Exists(local) ? local : null;
        }
    }
    public static string Config => Path.Combine(Roaming, "config.json");
    public static string Themes => Ensure(Path.Combine(Roaming, "themes"));
    public static string Plugins => Ensure(Path.Combine(Roaming, "plugins"));
    public static string PluginData => Ensure(Path.Combine(Roaming, "plugin-data"));
    public static string LogFile => Path.Combine(Local, "paddock.log");

    public static string Ensure(string dir) { Directory.CreateDirectory(dir); return dir; }
}

public static class Log
{
    static readonly object Gate = new();

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg, Exception? ex = null) => Write("FEHLER", ex is null ? msg : $"{msg}: {ex}");

    static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > 512 * 1024) file.Delete(); // klein halten
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}

/// <summary>Lädt/speichert die Einstellungen als JSON. Speichern ist entprellt und atomar.</summary>
public sealed class SettingsStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    readonly DispatcherTimer _debounce;
    public AppSettings Settings { get; private set; } = new();

    public SettingsStore()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); SaveNow(); };
    }

    public void Load()
    {
        try
        {
            if (File.Exists(AppPaths.Config))
                Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Config), Json) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("Konfiguration defekt, Sicherung angelegt", ex);
            try { File.Copy(AppPaths.Config, AppPaths.Config + ".defekt", true); } catch { }
            Settings = new();
        }
    }

    public void Save() { _debounce.Stop(); _debounce.Start(); }

    public void SaveNow()
    {
        try
        {
            var tmp = AppPaths.Config + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Settings, Json));
            File.Move(tmp, AppPaths.Config, true);
        }
        catch (Exception ex) { Log.Error("Speichern fehlgeschlagen", ex); }
    }
}
