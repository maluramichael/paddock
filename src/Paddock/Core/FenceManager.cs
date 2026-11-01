using System.Windows;
using System.Windows.Threading;
using Paddock.Interop;
using Paddock.Sdk;
using Paddock.Views;
using static Paddock.Interop.NativeMethods;

namespace Paddock.Core;

/// <summary>Verwaltet alle Bereiche: Anlegen, Entfernen, Ausblenden, Aufräumen.</summary>
public sealed class FenceManager
{
    readonly AppHost _host;
    readonly List<FenceWindow> _windows = [];
    readonly Dictionary<string, DateTime> _recentlyLeft = new(StringComparer.OrdinalIgnoreCase);
    bool _hidNativeIcons;

    public FenceManager(AppHost host) => _host = host;

    public IReadOnlyList<FenceWindow> Windows => _windows;
    AppSettings Settings => _host.Settings;
    public bool IsHidden => Settings.IsHidden;

    public void LoadAll()
    {
        foreach (var cfg in Settings.Fences.ToList())
        {
            if (cfg.Kind == FenceKind.Collection) Directory.CreateDirectory(cfg.FolderPath);
            Open(cfg);
        }
    }

    FenceWindow Open(FenceConfig cfg)
    {
        var w = new FenceWindow(cfg);
        _windows.Add(w);
        if (!IsHidden) w.Show();
        return w;
    }

    public IReadOnlyList<RECT> OtherRects(FenceWindow except) =>
        _windows.Where(w => w != except && w.IsVisible).Select(w => w.ScreenRect).ToList();

    // ------------------------------------------------------------------ Anlegen
    public FenceWindow Create(RECT r, string title, FenceKind kind, string? folder = null, string? tint = null)
    {
        if (IsHidden) ToggleHidden();
        var cfg = new FenceConfig
        {
            Title = title,
            Kind = kind,
            X = r.Left, Y = r.Top, Width = r.Width, Height = r.Height,
            Tint = tint,
            NavigateFolders = kind == FenceKind.Portal,
            Sort = kind == FenceKind.Portal ? FenceSort.Name : FenceSort.Manual,
            FolderPath = folder ?? CreateCollectionFolder(title),
        };
        Settings.Fences.Add(cfg);
        _host.Save();
        return Open(cfg);
    }

    string CreateCollectionFolder(string title)
    {
        var root = AppPaths.Ensure(Settings.CollectionsRoot);
        var path = UniqueDir(root, Sanitize(title));
        Directory.CreateDirectory(path);
        return path;
    }

    static string Sanitize(string name)
    {
        var s = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return s.Length == 0 ? "Bereich" : s;
    }

    static string UniqueDir(string root, string name)
    {
        var path = Path.Combine(root, name);
        for (int i = 2; Directory.Exists(path) || File.Exists(path); i++) path = Path.Combine(root, $"{name} ({i})");
        return path;
    }

    int Px(double dip, POINT at) => (int)Math.Round(dip * MonitorScale(at));

    static double MonitorScale(POINT pt)
    {
        try
        {
            var mon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            return GetDpiForMonitor(mon, 0, out var x, out _) == 0 ? x / 96.0 : 1;
        }
        catch { return 1; }
    }

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);

    /// <summary>Aus einem mit der rechten Maustaste aufgezogenen Rechteck – übernimmt darin liegende Desktop-Symbole.</summary>
    public void CreateFromDrawing(RECT r)
    {
        var center = new POINT(r.Left + r.Width / 2, r.Top + r.Height / 2);
        if (r.Width < Px(110, center) || r.Height < Px(60, center)) return;

        // Symbole im Rechteck merken, bevor das neue Fenster sie verdeckt
        var inside = DesktopShell.GetIcons()
            .Where(i => r.Contains(i.ScreenBounds.Left + i.ScreenBounds.Width / 2, i.ScreenBounds.Top + i.ScreenBounds.Height / 2))
            .Select(i => i.Label).ToList();

        var w = Create(r, "Neuer Bereich", FenceKind.Collection);
        if (inside.Count > 0)
        {
            var files = MapDesktopLabels(inside);
            if (files.Count > 0) ShellItems.Move(IntPtr.Zero, files, w.Config.FolderPath);
        }
        w.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, w.BeginRename);
    }

    /// <summary>Ordnet sichtbare Desktop-Beschriftungen den Dateien im Benutzer-Desktop zu.</summary>
    static List<string> MapDesktopLabels(IReadOnlyCollection<string> labels)
    {
        var wanted = new HashSet<string>(labels, StringComparer.CurrentCultureIgnoreCase);
        var result = new List<string>();
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(DesktopShell.UserDesktop))
            {
                var name = Path.GetFileName(path);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                if (wanted.Contains(DesktopShell.DisplayName(path)) || wanted.Contains(name)) result.Add(path);
            }
        }
        catch (Exception ex) { Log.Error("Desktop-Zuordnung", ex); }
        return result;
    }

    public void CreateBeside(FenceWindow? anchor, FenceKind kind)
    {
        string? folder = null;
        if (kind == FenceKind.Portal && (folder = Dialog.PickFolder()) is null) return;

        RECT r;
        if (anchor is not null)
        {
            var a = anchor.ScreenRect;
            var pt = new POINT(a.Left, a.Top);
            int gap = Px(10, pt);
            int h = anchor.Config.Collapsed ? anchor.Config.Height : a.Height;
            r = new RECT(a.Right + gap, a.Top, a.Right + gap + a.Width, a.Top + h);
            var work = GetWorkArea(pt);
            if (r.Right > work.Right) r = new RECT(a.Left, a.Top + h + gap, a.Right, a.Top + h + gap + h);
            r = Snapper.EnsureVisible(r, GetWorkAreas());
        }
        else r = FreeSpot(380, 240);

        var title = kind == FenceKind.Portal ? Path.GetFileName(folder!.TrimEnd('\\')) is { Length: > 0 } n ? n : folder : "Neuer Bereich";
        var w = Create(r, title, kind, folder);
        if (kind == FenceKind.Collection) w.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, w.BeginRename);
    }

    /// <summary>Sucht oben links auf dem Hauptbildschirm eine freie Stelle.</summary>
    RECT FreeSpot(double wDip, double hDip)
    {
        var work = GetWorkArea(new POINT(0, 0));
        var pt = new POINT(work.Left, work.Top);
        int w = Px(wDip, pt), h = Px(hDip, pt), gap = Px(16, pt);
        var taken = _windows.Where(x => x.IsVisible).Select(x => x.ScreenRect).ToList();
        for (int x = work.Right - w - gap; x >= work.Left; x -= w / 2)
            for (int y = work.Top + gap; y + h <= work.Bottom; y += h / 3)
            {
                var c = new RECT(x, y, x + w, y + h);
                if (!taken.Any(t => t.IntersectsWith(c))) return c;
            }
        return new RECT(work.Left + gap, work.Top + gap, work.Left + gap + w, work.Top + gap + h);
    }

    // ------------------------------------------------------------------ Umbenennen / Entfernen
    public void Rename(FenceWindow w, string title)
    {
        var old = w.Config.Title;
        w.Config.Title = title;
        if (w.Config.Kind == FenceKind.Collection)
        {
            // Sammelordner mitbenennen, solange er noch dem alten Titel entspricht
            var dir = w.Config.FolderPath.TrimEnd('\\');
            var parent = Path.GetDirectoryName(dir);
            if (parent is not null && Path.GetFileName(dir).StartsWith(Sanitize(old), StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var target = UniqueDir(parent, Sanitize(title));
                    Directory.Move(dir, target);
                    w.ChangeFolder(target);
                    RetargetRules(w);
                }
                catch (Exception ex) { Log.Info($"Ordner nicht umbenannt ({ex.Message})"); }
            }
        }
        _host.Save();
    }

    static void RetargetRules(FenceWindow w) { /* Regeln verweisen per Id – nichts zu tun */ }

    public void Remove(FenceWindow w)
    {
        var cfg = w.Config;
        if (cfg.Kind == FenceKind.Collection)
        {
            var items = Directory.Exists(cfg.FolderPath)
                ? Directory.EnumerateFileSystemEntries(cfg.FolderPath).Where(p => !Path.GetFileName(p).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)).ToList()
                : [];
            if (items.Count > 0)
            {
                var choice = Dialog.Choose("Bereich entfernen",
                    $"„{cfg.Title}“ enthält {items.Count} {(items.Count == 1 ? "Element" : "Elemente")}. Was soll damit passieren?",
                    "Auf den Desktop legen", "Im Ordner behalten");
                if (choice is null) return;
                if (choice == "a" && !ShellItems.Move(IntPtr.Zero, items, DesktopShell.UserDesktop)) return;
                if (choice == "b") ShellItems.Open(cfg.FolderPath);
            }
            else if (!Dialog.Confirm("Bereich entfernen", $"Den leeren Bereich „{cfg.Title}“ entfernen?", "Entfernen")) return;
            TryDeleteEmpty(cfg.FolderPath);
        }
        else if (!Dialog.Confirm("Portal entfernen", $"Das Portal „{cfg.Title}“ entfernen? Der Ordner selbst bleibt unverändert.", "Entfernen")) return;

        Settings.Fences.Remove(cfg);
        Settings.Rules.RemoveAll(r => r.TargetFenceId == cfg.Id);
        _windows.Remove(w);
        w.Close();
        _host.Save();
    }

    static void TryDeleteEmpty(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            var ini = Path.Combine(dir, "desktop.ini");
            if (Directory.EnumerateFileSystemEntries(dir).All(p => p.Equals(ini, StringComparison.OrdinalIgnoreCase)))
            {
                if (File.Exists(ini)) File.Delete(ini);
                Directory.Delete(dir);
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ Ausblenden
    public void ToggleHidden()
    {
        Settings.IsHidden = !Settings.IsHidden;
        foreach (var w in _windows) { if (Settings.IsHidden) w.FadeOut(); else w.FadeIn(); }

        if (Settings.IsHidden && Settings.QuickHideNativeIcons && DesktopShell.NativeIconsVisible)
        {
            DesktopShell.SetNativeIconsVisible(false);
            _hidNativeIcons = true;
        }
        else if (!Settings.IsHidden && _hidNativeIcons)
        {
            DesktopShell.SetNativeIconsVisible(true);
            _hidNativeIcons = false;
        }
        _host.UpdateTray();
    }

    public void RestoreNativeIcons()
    {
        if (_hidNativeIcons) DesktopShell.SetNativeIconsVisible(true);
        _hidNativeIcons = false;
    }

    public void SetAllCollapsed(bool collapsed)
    {
        foreach (var w in _windows.Where(w => w.Config.Collapsed != collapsed)) w.ToggleCollapsed();
    }

    public void ReattachAll()
    {
        foreach (var w in _windows) w.AttachToDesktop();
    }

    public void EnsureAllVisible()
    {
        var work = GetWorkAreas();
        foreach (var w in _windows)
        {
            var r = w.ScreenRect;
            var fixedRect = Snapper.EnsureVisible(r, work);
            if (!fixedRect.Equals(r))
                SetWindowPos(new System.Windows.Interop.WindowInteropHelper(w).Handle, IntPtr.Zero, fixedRect.Left, fixedRect.Top, fixedRect.Width, fixedRect.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    public void RefreshAll(bool icons = false)
    {
        if (icons) ShellIcons.Instance.Clear();
        foreach (var w in _windows) { if (icons) w.RefreshAppearance(); else w.Reload(); }
    }

    public FenceWindow? Find(Guid id) => _windows.FirstOrDefault(w => w.Id == id);

    // ------------------------------------------------------------------ Regeln: neu vs. verschoben
    /// <summary>Merkt sich Dateien, die gerade einen Bereich verlassen haben (damit Regeln sie nicht zurückholen).</summary>
    public void NoteLeft(IEnumerable<string> names)
    {
        var now = DateTime.UtcNow;
        foreach (var n in names) _recentlyLeft[n] = now;
        foreach (var old in _recentlyLeft.Where(kv => now - kv.Value > TimeSpan.FromMinutes(2)).Select(kv => kv.Key).ToList())
            _recentlyLeft.Remove(old);
    }

    public bool LeftRecently(string name) =>
        _recentlyLeft.TryGetValue(name, out var t) && DateTime.UtcNow - t < TimeSpan.FromMinutes(2);

    // ------------------------------------------------------------------ Desktop aufräumen
    public void OrganizeDesktop()
    {
        List<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(DesktopShell.UserDesktop)
                .Where(p =>
                {
                    var a = File.GetAttributes(p);
                    return !a.HasFlag(FileAttributes.Hidden) && !a.HasFlag(FileAttributes.System);
                }).ToList();
        }
        catch (Exception ex) { Log.Error("Desktop lesen", ex); return; }

        if (entries.Count == 0)
        {
            Dialog.Info("Desktop ist schon aufgeräumt", "Auf deinem Desktop liegen keine Dateien, die sortiert werden könnten.");
            return;
        }

        var groups = entries.GroupBy(p => FileKinds.Of(ItemInfo.From(p)))
            .OrderBy(g => Array.IndexOf(FileKinds.Ordered, g.Key)).ToList();
        var summary = string.Join(", ", groups.Select(g => $"{FileKinds.TitleFor(g.Key)} ({g.Count()})"));
        var (ok, autoRules) = Dialog.ConfirmWithOption("Desktop aufräumen",
            $"{entries.Count} Elemente werden in {groups.Count} Bereiche sortiert:\n{summary}\n\nDie Dateien landen in „{Settings.CollectionsRoot}“ und bleiben über die Bereiche auf dem Desktop erreichbar.",
            "Neue Dateien künftig automatisch einsortieren", true, "Aufräumen");
        if (!ok) return;

        var work = GetWorkArea(new POINT(0, 0));
        var pt = new POINT(work.Left, work.Top);
        int gap = Px(14, pt), width = Px(360, pt), x = work.Left + gap, y = work.Top + gap;

        foreach (var g in groups)
        {
            var title = FileKinds.TitleFor(g.Key);
            var existing = _windows.FirstOrDefault(w => w.Config.Kind == FenceKind.Collection && w.Config.Title.Equals(title, StringComparison.CurrentCultureIgnoreCase));
            if (existing is null)
            {
                int rows = (int)Math.Ceiling(g.Count() / 4.0);
                int height = Math.Min(Px(34 + 8 + rows * 96, pt), Px(420, pt));
                if (y + height > work.Bottom - gap) { y = work.Top + gap; x += width + gap; }
                existing = Create(new RECT(x, y, x + width, y + height), title, FenceKind.Collection, null, FileKinds.TintFor(g.Key));
                y += height + gap;
            }
            ShellItems.Move(IntPtr.Zero, g.ToList(), existing.Config.FolderPath, silent: true);
            existing.ScheduleRefresh();

            if (autoRules && g.Key is not FileKinds.Other and not FileKinds.Folder
                && !Settings.Rules.Any(r => r.ConditionId == "kind" && r.Parameter == g.Key))
                Settings.Rules.Add(new RuleConfig { ConditionId = "kind", Parameter = g.Key, TargetFenceId = existing.Id });
        }
        if (autoRules) Settings.RulesEnabled = true;
        _host.Save();
    }
}
