using System.Windows;
using System.Windows.Threading;
using Paddock.Interop;
using Paddock.Sdk;

namespace Paddock.Core;

/// <summary>
/// Beobachtet den Desktop-Ordner und sortiert neue Dateien per Regel in Bereiche.
/// Neu = vor Kurzem erstellt und nicht gerade erst aus einem Bereich herausgezogen.
/// </summary>
public sealed class RuleEngine : IDisposable
{
    readonly AppHost _host;
    readonly Dictionary<string, int> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly DispatcherTimer _timer;
    FileSystemWatcher? _watcher;

    public RuleEngine(AppHost host)
    {
        _host = host;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => ProcessPending();
    }

    public void Update()
    {
        bool need = _host.Settings.RulesEnabled && _host.Settings.Rules.Any(r => r.Enabled);
        if (need && _watcher is null)
        {
            try
            {
                _watcher = new FileSystemWatcher(DesktopShell.UserDesktop)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    EnableRaisingEvents = true,
                };
                _watcher.Created += (_, e) => Enqueue(e.FullPath);
                _watcher.Renamed += (_, e) => Enqueue(e.FullPath);
            }
            catch (Exception ex) { Log.Error("Desktop-Überwachung", ex); }
        }
        else if (!need && _watcher is not null)
        {
            _watcher.Dispose();
            _watcher = null;
        }
    }

    void Enqueue(string path) => Application.Current.Dispatcher.BeginInvoke(() =>
    {
        _pending[path] = 0;
        _timer.Stop(); _timer.Start();
    });

    void ProcessPending()
    {
        _timer.Stop();
        foreach (var (path, attempt) in _pending.ToList())
        {
            _pending.Remove(path);
            if (!File.Exists(path) && !Directory.Exists(path)) continue;
            var name = Path.GetFileName(path);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.StartsWith("~$")) continue;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".tmp" or ".crdownload" or ".part" or ".download" or ".partial") continue; // Download läuft noch
            if (_host.Fences.LeftRecently(name)) continue;
            try
            {
                if (DateTime.Now - File.GetCreationTime(path) > TimeSpan.FromMinutes(3)) continue; // verschoben, nicht neu
            }
            catch { continue; }

            if (Apply(path) == false && attempt < 3) _pending[path] = attempt + 1; // gesperrt → später erneut
        }
        if (_pending.Count > 0) _timer.Start();
    }

    /// <summary>true = verschoben, false = Regel passte aber Verschieben schlug fehl, null = keine Regel.</summary>
    bool? Apply(string path)
    {
        var item = ItemInfo.From(path);
        foreach (var rule in _host.Settings.Rules.Where(r => r.Enabled))
        {
            var cond = _host.Registry.Condition(rule.ConditionId);
            var fence = _host.Fences.Find(rule.TargetFenceId);
            if (cond is null || fence is null) continue;
            bool match;
            try { match = cond.Matches(item, rule.Parameter); }
            catch (Exception ex) { Log.Error($"Regel {rule.ConditionId}", ex); continue; }
            if (!match) continue;
            bool ok = ShellItems.Move(IntPtr.Zero, [path], fence.Config.FolderPath, silent: true);
            if (ok) fence.ScheduleRefresh();
            return ok;
        }
        return null;
    }

    /// <summary>Wendet alle Regeln auf den aktuellen Desktop an. Gibt die Anzahl verschobener Elemente zurück.</summary>
    public int ApplyToDesktop()
    {
        int moved = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(DesktopShell.UserDesktop).ToList())
        {
            var a = File.GetAttributes(path);
            if (a.HasFlag(FileAttributes.Hidden) || a.HasFlag(FileAttributes.System)) continue;
            if (Apply(path) == true) moved++;
        }
        return moved;
    }

    public void Dispose() => _watcher?.Dispose();
}
