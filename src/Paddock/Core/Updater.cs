using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace Paddock.Core;

/// <summary>
/// Sucht auf GitHub nach neuen Releases und installiert das MSI.
/// Läuft nur beim Start (verzögert) und danach höchstens einmal pro Tag – kein Dauer-Polling.
/// </summary>
public sealed class Updater
{
    const string Repo = "maluramichael/paddock";
    public const string ReleasesUrl = $"https://github.com/{Repo}/releases/latest";

    readonly AppHost _host;
    readonly DispatcherTimer _daily;
    bool _busy;

    public Updater(AppHost host)
    {
        _host = host;
        _daily = new DispatcherTimer { Interval = TimeSpan.FromHours(24) };
        _daily.Tick += async (_, _) => await CheckAsync(interactive: false);
    }

    public static Version Current => AppHost.Version is { } v ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : new Version(0, 0, 0);

    /// <summary>Installiert über das MSI? (Nur dann kann sich Paddock selbst ersetzen.)</summary>
    public static bool IsInstalled =>
        !AppPaths.IsPortable &&
        AppContext.BaseDirectory.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase);

    public string Status { get; private set; } = "";
    public event Action? StatusChanged;

    void SetStatus(string s)
    {
        Status = s;
        StatusChanged?.Invoke();
    }

    public void Start()
    {
        if (!_host.Settings.AutoUpdate) return;
        // Nicht den Start ausbremsen: erst nach einer Weile im Hintergrund prüfen
        var delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        delay.Tick += async (_, _) => { delay.Stop(); await CheckAsync(interactive: false); };
        delay.Start();
        _daily.Start();
    }

    public void SettingsChanged()
    {
        if (_host.Settings.AutoUpdate) _daily.Start(); else _daily.Stop();
    }

    sealed record Release(Version Version, string Tag, string? MsiUrl, string? MsiName, string? Sha256, long Size);

    /// <summary>
    /// Prüft auf ein neueres Release. interactive = vom Benutzer ausgelöst (zeigt auch „aktuell“ an
    /// und fragt vor der Installation); sonst still und – wenn eingestellt – automatisch installieren.
    /// </summary>
    public async Task CheckAsync(bool interactive)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            SetStatus("Suche nach Updates …");
            var release = await FetchLatestAsync();
            _host.Settings.LastUpdateCheck = DateTime.Now;
            _host.Save();

            if (release is null || release.Version <= Current)
            {
                SetStatus($"Paddock {Current.ToString(3)} ist aktuell.");
                if (interactive) _host.Tray.ShowBalloon("Paddock ist aktuell", $"Version {Current.ToString(3)} ist die neueste.");
                return;
            }

            var v = release.Version.ToString(3);
            if (!IsInstalled || release.MsiUrl is null)
            {
                SetStatus($"Version {v} ist verfügbar.");
                _host.Tray.ShowBalloon($"Paddock {v} ist verfügbar",
                    AppPaths.IsPortable ? "Die portable Version bitte von GitHub neu herunterladen." : "Download auf GitHub.");
                if (interactive) Process.Start(new ProcessStartInfo(ReleasesUrl) { UseShellExecute = true });
                return;
            }

            if (interactive && !Views.Dialog.Confirm($"Paddock {v} ist da",
                    $"Du nutzt Version {Current.ToString(3)}. Jetzt aktualisieren? Paddock startet danach von selbst neu. " +
                    "Windows fragt kurz nach Administratorrechten.", "Jetzt aktualisieren", "Später"))
            {
                SetStatus($"Version {v} ist verfügbar.");
                return;
            }

            SetStatus($"Lade Version {v} …");
            var msi = await DownloadAsync(release);
            SetStatus($"Installiere Version {v} …");
            _host.Tray.ShowBalloon($"Paddock wird auf {v} aktualisiert", "Das dauert nur einen Moment, danach geht es von selbst weiter.");
            Install(msi);
        }
        catch (Exception ex)
        {
            Log.Error("Update", ex);
            SetStatus("Update-Prüfung fehlgeschlagen: " + ex.Message);
            if (interactive) _host.Tray.ShowBalloon("Update fehlgeschlagen", ex.Message);
        }
        finally { _busy = false; }
    }

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Paddock/{Current.ToString(3)} (+https://github.com/{Repo})");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    static async Task<Release?> FetchLatestAsync()
    {
        using var http = CreateClient();
        using var res = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;

        string? url = null, name = null, sha = null;
        long size = 0;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var n = asset.GetProperty("name").GetString() ?? "";
            if (!n.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) continue;
            url = asset.GetProperty("browser_download_url").GetString();
            name = n;
            size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            // GitHub liefert für Assets eine SHA-256-Prüfsumme ("sha256:…")
            if (asset.TryGetProperty("digest", out var dig) && dig.GetString() is { } digest && digest.StartsWith("sha256:"))
                sha = digest["sha256:".Length..];
            break;
        }
        return new Release(new Version(version.Major, version.Minor, Math.Max(version.Build, 0)), tag, url, name, sha, size);
    }

    static async Task<string> DownloadAsync(Release r)
    {
        var dir = AppPaths.Ensure(Path.Combine(Path.GetTempPath(), "Paddock-Update"));
        foreach (var old in Directory.EnumerateFiles(dir, "*.msi")) { try { File.Delete(old); } catch { } }
        var target = Path.Combine(dir, r.MsiName ?? $"Paddock-{r.Version.ToString(3)}-x64.msi");

        using (var http = CreateClient())
        using (var stream = await http.GetStreamAsync(r.MsiUrl))
        using (var file = File.Create(target))
            await stream.CopyToAsync(file);

        var info = new FileInfo(target);
        if (r.Size > 0 && info.Length != r.Size) throw new InvalidOperationException("Download unvollständig.");
        if (r.Sha256 is not null)
        {
            using var fs = File.OpenRead(target);
            var hash = Convert.ToHexString(SHA256.HashData(fs));
            if (!hash.Equals(r.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Prüfsumme des Downloads stimmt nicht.");
        }
        return target;
    }

    /// <summary>
    /// Startet msiexec mit einfacher Fortschrittsanzeige und beendet Paddock.
    /// Das MSI startet Paddock nach der Installation selbst wieder.
    /// </summary>
    void Install(string msi)
    {
        var log = Path.Combine(AppPaths.Local, "update.log");
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Paddock.exe");
        // msiexec fragt selbst nach Adminrechten. Danach startet die Kommandozeile Paddock wieder –
        // mit normalen Rechten (nicht das MSI, sonst liefe Paddock evtl. erhöht und Drag & Drop aus
        // dem Explorer wäre blockiert). Auch bei Abbruch (UAC abgelehnt) kommt die alte Version zurück.
        var cmd = $"/c start \"\" /wait msiexec.exe /i \"{msi}\" NOLAUNCH=1 /passive /norestart /l*v \"{log}\" & start \"\" \"{exe}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", cmd) { UseShellExecute = false, CreateNoWindow = true });
        _host.Store.SaveNow();
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Application.Current.Shutdown());
    }
}
