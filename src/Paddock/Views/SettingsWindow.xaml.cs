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
        var desktop = DesktopSnapshot();
        _rules.Clear();
        var list = _host.Settings.Rules;
        for (int i = 0; i < list.Count; i++)
            _rules.Add(new RuleRow(list[i], _host, fences, desktop) { Number = i + 1, CanMoveUp = i > 0, CanMoveDown = i < list.Count - 1 });
        RuleList.ItemsSource = _rules;
        NoFencesHint.Visibility = fences.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddRuleButton.IsEnabled = fences.Count > 0;
        RulesEmpty.Visibility = list.Count == 0 && fences.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildRuleTemplates(fences);
    }

    /// <summary>Aktueller Desktop-Inhalt für die Live-Vorschau der Regeln (einmal pro Aufbau gelesen).</summary>
    static IReadOnlyList<ItemInfo> DesktopSnapshot()
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(DesktopShell.UserDesktop)
                .Where(p => !File.GetAttributes(p).HasFlag(FileAttributes.Hidden) && !File.GetAttributes(p).HasFlag(FileAttributes.System))
                .Select(ItemInfo.From).ToList();
        }
        catch { return []; }
    }

    /// <summary>Schnellvorlagen für den leeren Zustand – Ziel ist ein passend benannter Bereich, sonst der erste.</summary>
    void BuildRuleTemplates(IReadOnlyList<FenceOption> fences)
    {
        RuleTemplates.Children.Clear();
        (string Label, string Condition, string Parameter, string[] FenceHints)[] templates =
        [
            ("Programme & Verknüpfungen", "kind", FileKinds.App, ["programm", "app", "spiel"]),
            ("Bilder", "kind", FileKinds.Image, ["bild", "foto"]),
            ("Dokumente", "kind", FileKinds.Document, ["dokument", "office"]),
            ("PDFs", "extension", ".pdf", ["dokument", "pdf"]),
            ("Screenshots", "name", "*screenshot*, Bildschirmfoto*", ["screenshot", "bild"]),
            ("Archive (ZIP, 7z …)", "kind", FileKinds.Archive, ["archiv", "download"]),
        ];
        foreach (var t in templates)
        {
            var b = new Button { Content = "+  " + t.Label, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(12, 6, 12, 6) };
            b.SetResourceReference(StyleProperty, "P.Button");
            b.Click += (_, _) =>
            {
                var target = fences.FirstOrDefault(f => t.FenceHints.Any(h => f.Title.Contains(h, StringComparison.CurrentCultureIgnoreCase)))
                             ?? fences.FirstOrDefault();
                if (target is not null) AddRule(t.Condition, t.Parameter, target.Id);
            };
            RuleTemplates.Children.Add(b);
        }
    }

    void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var target = _host.Fences.Windows.FirstOrDefault();
        if (target is not null) AddRule("kind", FileKinds.Image, target.Id);
    }

    void AddRule(string condition, string parameter, Guid target)
    {
        _host.Settings.Rules.Add(new RuleConfig { ConditionId = condition, Parameter = parameter, TargetFenceId = target });
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
        BuildRules();
    }

    void MoveRuleUp_Click(object sender, RoutedEventArgs e) => MoveRule(sender, -1);
    void MoveRuleDown_Click(object sender, RoutedEventArgs e) => MoveRule(sender, +1);

    void MoveRule(object sender, int delta)
    {
        if ((sender as FrameworkElement)?.DataContext is not RuleRow row) return;
        var list = _host.Settings.Rules;
        int i = list.IndexOf(row.Config), j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        _host.Save();
        BuildRules();
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

public enum RuleStatus { Idle, Match, Problem }

/// <summary>Eine Regel im Editor – mit Live-Vorschau, wie viele Desktop-Dateien sie gerade trifft.</summary>
public sealed class RuleRow : INotifyPropertyChanged
{
    readonly AppHost _host;
    readonly IReadOnlyList<ItemInfo> _desktop;
    public RuleConfig Config { get; }
    public IReadOnlyList<IRuleCondition> Conditions { get; }
    public IReadOnlyList<FenceOption> Fences { get; }

    public int Number { get; init; }
    public bool CanMoveUp { get; init; }
    public bool CanMoveDown { get; init; }

    public RuleRow(RuleConfig config, AppHost host, IReadOnlyList<FenceOption> fences, IReadOnlyList<ItemInfo> desktop)
    {
        Config = config;
        _host = host;
        _desktop = desktop;
        Conditions = host.Registry.Conditions;
        Fences = fences;
    }

    IRuleCondition? Condition => _host.Registry.Condition(Config.ConditionId);

    public bool Enabled { get => Config.Enabled; set { Config.Enabled = value; Changed(); } }

    public string ConditionId
    {
        get => Config.ConditionId;
        set
        {
            if (Config.ConditionId == value) return;
            Config.ConditionId = value;
            // Wechsel auf eine Auswahl-Bedingung: ungültigen Freitext durch den ersten Wert ersetzen
            if (Condition?.Choices is { Count: > 0 } choices && !choices.Contains(Config.Parameter)) Config.Parameter = choices[0];
            else if (Condition?.Choices is null && Conditions.Any(c => c.Choices?.Contains(Config.Parameter) == true)) Config.Parameter = "";
            Changed();
            Raise(nameof(Parameter)); Raise(nameof(Choices)); Raise(nameof(HasChoices)); Raise(nameof(HasText)); Raise(nameof(Hint)); Raise(nameof(ShowHint));
        }
    }

    public string Parameter { get => Config.Parameter; set { Config.Parameter = value ?? ""; Changed(); Raise(nameof(ShowHint)); } }
    public Guid TargetFenceId { get => Config.TargetFenceId; set { Config.TargetFenceId = value; Changed(); } }

    public IReadOnlyList<string>? Choices => Condition?.Choices;
    public bool HasChoices => Choices is { Count: > 0 };
    public bool HasText => !HasChoices;
    public string Hint => Condition?.ParameterHint ?? "";
    public bool ShowHint => HasText && string.IsNullOrEmpty(Config.Parameter);

    // ---- Live-Vorschau
    IReadOnlyList<ItemInfo> Matches()
    {
        var cond = Condition;
        if (cond is null || string.IsNullOrWhiteSpace(Config.Parameter)) return [];
        try { return _desktop.Where(i => cond.Matches(i, Config.Parameter)).ToList(); }
        catch { return []; }
    }

    public RuleStatus Status =>
        Condition is null || string.IsNullOrWhiteSpace(Config.Parameter) || Fences.All(f => f.Id != Config.TargetFenceId) ? RuleStatus.Problem
        : Matches().Count > 0 ? RuleStatus.Match : RuleStatus.Idle;

    public string StatusText
    {
        get
        {
            if (Condition is null) return "Bedingung nicht verfügbar (Plugin fehlt?)";
            if (string.IsNullOrWhiteSpace(Config.Parameter)) return "Wert fehlt";
            if (Fences.All(f => f.Id != Config.TargetFenceId)) return "Ziel-Bereich fehlt";
            int n = Matches().Count;
            return n switch
            {
                0 => "Trifft gerade nichts",
                1 => "Trifft gerade 1 Datei",
                _ => $"Trifft gerade {n} Dateien",
            };
        }
    }

    public string? StatusDetails
    {
        get
        {
            var m = Matches();
            if (m.Count == 0) return "Keine Datei auf dem Desktop passt gerade zu dieser Regel.";
            var names = m.Take(12).Select(i => "• " + i.Name);
            return string.Join("\n", names) + (m.Count > 12 ? $"\n… und {m.Count - 12} weitere" : "");
        }
    }

    void Changed([CallerMemberName] string? n = null)
    {
        Raise(n);
        Raise(nameof(Status)); Raise(nameof(StatusText)); Raise(nameof(StatusDetails));
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
