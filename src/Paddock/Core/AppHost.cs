using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Win32;
using Paddock.Interop;
using Paddock.Sdk;
using Paddock.Views;

namespace Paddock.Core;

/// <summary>Verbindet alle Dienste der App. Lebt so lange wie der Prozess.</summary>
public sealed class AppHost : IDisposable
{
    public SettingsStore Store { get; } = new();
    public AppSettings Settings => Store.Settings;
    public ExtensionRegistry Registry { get; } = new();
    public HelperWindow Helper { get; private set; } = null!;
    public TrayIcon Tray { get; private set; } = null!;
    public FenceManager Fences { get; }
    public RuleEngine Rules { get; }
    DesktopGestures _gestures = null!;
    DrawPreview? _preview;
    SettingsWindow? _settingsWindow;

    public static readonly Version Version = typeof(AppHost).Assembly.GetName().Version ?? new Version(1, 0);

    public AppHost()
    {
        Fences = new FenceManager(this);
        Rules = new RuleEngine(this);
    }

    public void Start(string[] args)
    {
        Store.Load();
        ThemeManager.Instance.Initialize(Settings.Theme);

        Helper = new HelperWindow();
        Helper.TrayMessage += OnTray;
        Helper.ActivateRequested += ShowSettings;
        Helper.ExplorerRestarted += () =>
        {
            Tray.Add();
            Fences.ReattachAll();
        };
        Helper.DisplayChanged += () => Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, Fences.EnsureAllVisible);
        Tray = new TrayIcon(Helper, "Paddock – Desktop-Bereiche");

        Registry.LoadPlugins(p => new PluginHost(this, p), Settings.DisabledPlugins);

        Fences.LoadAll();

        _gestures = new DesktopGestures(Application.Current.Dispatcher);
        _gestures.DesktopDoubleClicked += () => Fences.ToggleHidden();
        _gestures.DrawStarted += r => (_preview ??= new DrawPreview()).Place(r);
        _gestures.DrawUpdated += r => _preview?.Place(r);
        _gestures.DrawCompleted += r =>
        {
            _preview?.Close();
            _preview = null;
            Fences.CreateFromDrawing(r);
        };
        ApplyBehaviorSettings();

        if (!Settings.FirstRunDone)
        {
            Settings.FirstRunDone = true;
            Save();
            Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => new WelcomeWindow().Show());
        }
        else if (args.Contains("--settings")) ShowSettings();

        // Nach dem Start Speicher zurückgeben, den nur das Hochfahren brauchte
        var trim = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        trim.Tick += (_, _) => { trim.Stop(); TrimMemory(); };
        trim.Start();
    }

    public void Save() => Store.Save();

    public void ApplyBehaviorSettings()
    {
        _gestures.DoubleClickEnabled = Settings.QuickHide;
        _gestures.DrawEnabled = Settings.DrawToCreate;
        _gestures.Update();
        Rules.Update();
    }

    public static void TrimMemory()
    {
        GC.Collect(2, GCCollectionMode.Optimized, false, true);
        NativeMethods.EmptyWorkingSet(NativeMethods.GetCurrentProcess());
    }

    public void RunSafe(string what, Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log.Error(what, ex);
            Tray.ShowBalloon("Erweiterung fehlgeschlagen", $"{what}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ Infobereich
    void OnTray(int msg)
    {
        if (msg is TrayIcon.WM_LBUTTONUP_ or TrayIcon.NIN_SELECT or TrayIcon.WM_CONTEXTMENU_ or TrayIcon.WM_RBUTTONUP_) ShowTrayMenu();
        else if (msg == TrayIcon.WM_LBUTTONDBLCLK_) ShowSettings();
    }

    public void UpdateTray() => Tray.SetTip(Fences.IsHidden ? "Paddock – ausgeblendet (Doppelklick auf den Desktop)" : "Paddock – Desktop-Bereiche");

    void ShowTrayMenu()
    {
        NativeMethods.SetForegroundWindow(Helper.Handle);
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        MenuItem Item(string header, string glyph, Action click, bool? check = null, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, Icon = glyph, IsEnabled = enabled };
            if (check is { } c) { mi.IsCheckable = true; mi.IsChecked = c; }
            mi.Click += (_, _) => click();
            return mi;
        }

        var head = new MenuItem { Header = "Paddock", IsEnabled = false, FontWeight = FontWeights.SemiBold, Icon = "" };
        menu.Items.Add(head);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Neuer Bereich", "", () => Fences.CreateBeside(null, FenceKind.Collection)));
        menu.Items.Add(Item("Neues Ordner-Portal …", "", () => Fences.CreateBeside(null, FenceKind.Portal)));
        menu.Items.Add(Item("Desktop aufräumen …", "", Fences.OrganizeDesktop));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Fences.IsHidden ? "Bereiche einblenden" : "Bereiche ausblenden", Fences.IsHidden ? "" : "", Fences.ToggleHidden));
        menu.Items.Add(Item("Alle einklappen", "", () => Fences.SetAllCollapsed(true), enabled: Fences.Windows.Count > 0));
        menu.Items.Add(Item("Alle ausklappen", "", () => Fences.SetAllCollapsed(false), enabled: Fences.Windows.Count > 0));
        menu.Items.Add(Item("Desktop-Symbole anzeigen", "", () => DesktopShell.SetNativeIconsVisible(!DesktopShell.NativeIconsVisible), DesktopShell.NativeIconsVisible));

        var themes = new MenuItem { Header = "Theme", Icon = "" };
        foreach (var t in ThemeManager.Instance.Themes.OrderBy(t => !t.BuiltIn).ThenBy(t => t.Name))
            themes.Items.Add(Item(t.Name, "", () => SetTheme(t.Id), t.Id.Equals(Settings.Theme, StringComparison.OrdinalIgnoreCase)));
        menu.Items.Add(themes);

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Einstellungen …", "", ShowSettings));
        menu.Items.Add(Item("Beenden", "", Exit));
        menu.IsOpen = true;
    }

    public void SetTheme(string id)
    {
        Settings.Theme = id;
        ThemeManager.Instance.Apply(id);
        Save();
    }

    public void ShowSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => { _settingsWindow = null; Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, TrimMemory); };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void Exit()
    {
        Store.SaveNow();
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        Store.SaveNow();
        Fences.RestoreNativeIcons();
        _gestures?.Dispose();
        Rules.Dispose();
        Tray?.Dispose();
        Helper?.Dispose();
    }

    // ------------------------------------------------------------------ Autostart
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool Autostart
    {
        get
        {
            using var k = Registry_.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("Paddock") is string;
        }
        set
        {
            using var k = Registry_.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("Paddock", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("Paddock", false);
        }
    }

    // Alias, weil "Registry" hier die Erweiterungs-Registry ist
    static class Registry_ { public static RegistryKey CurrentUser => Microsoft.Win32.Registry.CurrentUser; }
}

/// <summary>Die Sicht eines Plugins auf Paddock.</summary>
sealed class PluginHost(AppHost host, IPaddockPlugin plugin) : IPaddockHost
{
    public Version HostVersion => AppHost.Version;
    public string DataDirectory => AppPaths.Ensure(Path.Combine(AppPaths.PluginData, string.Concat(plugin.Id.Split(Path.GetInvalidFileNameChars()))));
    public IReadOnlyList<IFenceInfo> Fences => host.Fences.Windows;
    public void RegisterRuleCondition(IRuleCondition condition) => host.Registry.Conditions.Add(condition);
    public void RegisterFenceCommand(IFenceCommand command) => host.Registry.FenceCommands.Add(command);
    public void RegisterItemCommand(IItemCommand command) => host.Registry.ItemCommands.Add(command);
    public void Notify(string title, string message) => Application.Current.Dispatcher.BeginInvoke(() => host.Tray.ShowBalloon(title, message));
    public void Log(string message) => Core.Log.Info($"[{plugin.Id}] {message}");
    public void Refresh(Guid fenceId) => Application.Current.Dispatcher.BeginInvoke(() => host.Fences.Find(fenceId)?.ScheduleRefresh());
}
