using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Paddock.Core;
using Paddock.Interop;
using Paddock.Sdk;

namespace Paddock.Views;

public partial class SettingsWindow : Window
{
    readonly AppHost _host;
    readonly ObservableCollection<RuleRow> _rules = [];

    public SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        DataContext = host.Settings;
        if (App.Showcase) Topmost = true;
        AutostartToggle.IsChecked = AppHost.Autostart;
        CollectionsPath.Text = host.Settings.CollectionsRoot;
        VersionText.Text = $"Version {AppHost.Version.ToString(3)}";

        BuildThemes();
        ThemeManager.Instance.Changed += OnThemeChanged;
        BuildRules();
        BuildPlugins();
        BuildCheats();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
            ApplyBackdrop();
        };
        Closed += (_, _) => ThemeManager.Instance.Changed -= OnThemeChanged;
    }

    void ApplyBackdrop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        bool dark = ThemeManager.Instance.Current.Dark;
        Backdrop.ApplyMica(hwnd, dark);
        // Ohne Mica (Windows 10) wäre der Hintergrund schwarz/leer – dann eine solide Fläche zeigen
        if (Environment.OSVersion.Version.Build < 22000) SetResourceReference(BackgroundProperty, "P.Ui.Solid");
    }

    void OnThemeChanged()
    {
        ApplyBackdrop();
        BuildThemes();
    }

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem { Tag: string tag } || General is null) return;
        foreach (var page in new FrameworkElement[] { General, Appearance, Rules, Plugins, About })
            page.Visibility = page.Name == tag ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "About") UpdateStats();
        if (tag == "Rules") BuildRules();
        PageScroller.ScrollToTop();
    }

    // ------------------------------------------------------------------ Allgemein
    void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource == AutostartToggle) return;
        _host.ApplyBehaviorSettings();
        _host.Save();
        if (e.OriginalSource is CheckBox cb && cb.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.ResolvedSourcePropertyName
                is nameof(AppSettings.ShowHiddenFiles) or nameof(AppSettings.ShowThumbnails))
            _host.Fences.RefreshAll(icons: true);
    }

    void Autostart_Click(object sender, RoutedEventArgs e)
    {
        try { AppHost.Autostart = AutostartToggle.IsChecked == true; }
        catch (Exception ex) { Log.Error("Autostart", ex); AutostartToggle.IsChecked = AppHost.Autostart; }
    }

    void OpenCollections_Click(object sender, RoutedEventArgs e) =>
        ShellItems.Open(AppPaths.Ensure(_host.Settings.CollectionsRoot));

    void ChangeCollections_Click(object sender, RoutedEventArgs e)
    {
        if (Dialog.PickFolder(_host.Settings.CollectionsRoot) is not { } folder) return;
        _host.Settings.CollectionsRoot = folder;
        CollectionsPath.Text = folder;
        _host.Save();
    }

    // ------------------------------------------------------------------ Darstellung
    void BuildThemes()
    {
        var current = _host.Settings.Theme;
        ThemeList.ItemsSource = ThemeManager.Instance.Themes
            .OrderBy(t => !t.BuiltIn).ThenBy(t => t.Id == "mitternacht" ? 0 : 1).ThenBy(t => t.Name)
            .Select(t => new ThemeCard(t, t.Id.Equals(current, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    void ThemeCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ThemeCard card) _host.SetTheme(card.Id);
    }

    void ExportTheme_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var file = ThemeManager.Instance.ExportTemplate(_host.Settings.Theme);
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Kein Programm für .json verknüpft → Ordner zeigen
            Log.Info($"Theme öffnen: {ex.Message}");
            ShellItems.Open(AppPaths.Themes);
        }
    }

    void OpenThemes_Click(object sender, RoutedEventArgs e) => ShellItems.Open(AppPaths.Themes);

    // ------------------------------------------------------------------ Regeln
    void BuildRules()
    {
        var fences = _host.Fences.Windows.Select(w => new FenceOption(w.Id, w.Config.Title)).ToList();
        _rules.Clear();
        foreach (var r in _host.Settings.Rules) _rules.Add(new RuleRow(r, _host, fences));
        RuleList.ItemsSource = _rules;
        NoFencesHint.Visibility = fences.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddRuleButton.IsEnabled = fences.Count > 0;
    }

    void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var target = _host.Fences.Windows.FirstOrDefault();
        if (target is null) return;
        var rule = new RuleConfig { ConditionId = "extension", Parameter = "", TargetFenceId = target.Id };
        _host.Settings.Rules.Add(rule);
        _host.Settings.RulesEnabled = true;
        RulesToggle.IsChecked = true;
        _host.ApplyBehaviorSettings();
        _host.Save();
        BuildRules();
    }

    void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RuleRow row) return;
        _host.Settings.Rules.Remove(row.Config);
        _host.ApplyBehaviorSettings();
        _host.Save();
        _rules.Remove(row);
    }

    void ApplyRules_Click(object sender, RoutedEventArgs e)
    {
        int n = _host.Rules.ApplyToDesktop();
        Dialog.Info("Regeln angewendet", n == 0 ? "Auf dem Desktop lag nichts, das zu einer Regel passt." : $"{n} {(n == 1 ? "Element wurde" : "Elemente wurden")} einsortiert.");
    }

    // ------------------------------------------------------------------ Erweiterungen
    void BuildPlugins()
    {
        var rows = _host.Registry.Plugins.Select(p => new PluginRow(p, _host)).ToList();
        PluginList.ItemsSource = rows;
        NoPlugins.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OpenPlugins_Click(object sender, RoutedEventArgs e) => ShellItems.Open(AppPaths.Plugins);

    void Restart_Click(object sender, RoutedEventArgs e)
    {
        _host.Store.SaveNow();
        var exe = Environment.ProcessPath!;
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 1 >nul & start \"\" \"{exe}\" --settings")
        { CreateNoWindow = true, UseShellExecute = false });
        Application.Current.Shutdown();
    }

    // ------------------------------------------------------------------ Info
    void UpdateStats()
    {
        using var p = Process.GetCurrentProcess();
        var items = _host.Fences.Windows.Sum(w => Directory.Exists(w.FolderPath) ? Directory.EnumerateFileSystemEntries(w.FolderPath).Count() : 0);
        StatsText.Text =
            $"{_host.Fences.Windows.Count} Bereiche mit {items} Elementen\n" +
            $"Arbeitsspeicher: {p.WorkingSet64 / 1024 / 1024} MB (privat {p.PrivateMemorySize64 / 1024 / 1024} MB)\n" +
            $"CPU-Zeit seit Start: {p.TotalProcessorTime.TotalSeconds:0.0} s · Threads: {p.Threads.Count}";
    }

    void BuildCheats()
    {
        (string, string)[] rows =
        [
            ("Rechte Maustaste + Ziehen", "Neuen Bereich auf dem Desktop zeichnen"),
            ("Doppelklick auf freien Desktop", "Alles aus- und einblenden"),
            ("Klick auf den Titel", "Bereich ein-/ausklappen"),
            ("Titel ziehen", "Bereich verschieben (Alt = ohne Einrasten)"),
            ("Rechtsklick auf den Titel", "Farbe, Ansicht, Sortierung, Sperren …"),
            ("Rechtsklick auf eine Datei", "Explorer-Kontextmenü (Umschalt = erweitert)"),
            ("Strg / Umschalt + Klick", "Mehrere Elemente auswählen"),
            ("Ziehen mit Strg / Alt", "Kopieren / Verknüpfung erstellen"),
            ("Doppelklick aufs Tray-Symbol", "Einstellungen öffnen"),
        ];
        for (int i = 0; i < rows.Length; i++)
        {
            Cheats.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = new TextBlock { Text = rows[i].Item1, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 12, 4), TextWrapping = TextWrapping.Wrap };
            var val = new TextBlock { Text = rows[i].Item2, Margin = new Thickness(0, 4, 0, 4), TextWrapping = TextWrapping.Wrap };
            val.SetResourceReference(TextBlock.ForegroundProperty, "P.Ui.Muted");
            Grid.SetRow(key, i); Grid.SetRow(val, i); Grid.SetColumn(val, 1);
            Cheats.Children.Add(key); Cheats.Children.Add(val);
        }
    }

    void GitHub_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://github.com/maluramichael/paddock") { UseShellExecute = true });

    void OpenConfig_Click(object sender, RoutedEventArgs e) => ShellItems.Open(AppPaths.Roaming);
    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(AppPaths.LogFile)) ShellItems.Open(AppPaths.LogFile); else ShellItems.Open(AppPaths.Local);
    }
}

// ---------------------------------------------------------------------- Zeilen-Modelle

public sealed class ThemeCard(Theme t, bool selected)
{
    static Brush B(string s) { var b = new SolidColorBrush(ThemeManager.ParseColor(s, Colors.Transparent)); b.Freeze(); return b; }

    public string Id => t.Id;
    public string Name => t.Name + (t.BuiltIn ? "" : "  ·  eigenes");
    public string Description => t.Description ?? "";
    public bool IsSelected => selected;
    public Brush Tint
    {
        get
        {
            // Unschärfe lässt sich in der Vorschau nicht zeigen → Deckkraft leicht anheben
            var c = ThemeManager.WithOpacity(ThemeManager.ParseColor(t.Tint, Colors.Black), Math.Min(0.92, t.TintOpacity + (t.Backdrop == BackdropKind.Solid ? 0 : 0.3)));
            var b = new SolidColorBrush(c); b.Freeze(); return b;
        }
    }
    public Brush Border => B(t.Border);
    public Brush Accent => B(t.Accent);
    public Brush TitleBackground => B(t.Title.Background);
    public Brush TitleForeground => B(t.Title.Foreground);
    public Brush ItemForeground => B(t.Item.Foreground);
    public string TitleSample => t.Title.Uppercase ? "PROGRAMME" : "Programme";
    public HorizontalAlignment TitleAlignment => t.Title.Alignment == "center" ? HorizontalAlignment.Center : t.Title.Alignment == "right" ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public CornerRadius Radius => new(t.Corners switch { CornerKind.Round => 6, CornerKind.Small => 3, _ => 0 });
}

public sealed record FenceOption(Guid Id, string Title);

public sealed class RuleRow : INotifyPropertyChanged
{
    readonly AppHost _host;
    public RuleConfig Config { get; }
    public IReadOnlyList<IRuleCondition> Conditions { get; }
    public IReadOnlyList<FenceOption> Fences { get; }

    public RuleRow(RuleConfig config, AppHost host, IReadOnlyList<FenceOption> fences)
    {
        Config = config;
        _host = host;
        Conditions = host.Registry.Conditions;
        Fences = fences;
    }

    public bool Enabled { get => Config.Enabled; set { Config.Enabled = value; Changed(); } }
    public string ConditionId { get => Config.ConditionId; set { Config.ConditionId = value; Changed(); Raise(nameof(Hint)); } }
    public string Parameter { get => Config.Parameter; set { Config.Parameter = value; Changed(); } }
    public Guid TargetFenceId { get => Config.TargetFenceId; set { Config.TargetFenceId = value; Changed(); } }
    public string Hint => _host.Registry.Condition(Config.ConditionId)?.ParameterHint ?? "";

    void Changed([CallerMemberName] string? n = null)
    {
        Raise(n);
        _host.ApplyBehaviorSettings();
        _host.Save();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string? n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class PluginRow(ExtensionRegistry.LoadedPlugin p, AppHost host)
{
    public string Name => p.Plugin.DisplayName;
    public string Description => p.Plugin.Description;
    public string Details => $"{p.Plugin.Id} · {p.Source}";
    public string? Error => p.Error is null ? null : "Fehler beim Laden: " + p.Error;
    public bool Enabled
    {
        get => !host.Settings.DisabledPlugins.Contains(p.Plugin.Id);
        set
        {
            host.Settings.DisabledPlugins.Remove(p.Plugin.Id);
            if (!value) host.Settings.DisabledPlugins.Add(p.Plugin.Id);
            host.Save();
        }
    }
}
