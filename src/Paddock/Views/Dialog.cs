using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Paddock.Core;
using Paddock.Interop;

namespace Paddock.Views;

/// <summary>Kleine, zum Theme passende Dialoge (statt der grauen Standard-MessageBox).</summary>
public sealed class Dialog : Window
{
    readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
    readonly StackPanel _body = new();
    TextBox? _input;
    CheckBox? _check;
    string? _result;

    Dialog(string title, string message)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;
        Title = title;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 40, GlassFrameThickness = new Thickness(0), ResizeBorderThickness = new Thickness(0), UseAeroCaptionButtons = false });

        var root = new Border { Padding = new Thickness(24, 20, 24, 20) };
        root.SetResourceReference(Border.BackgroundProperty, "P.Ui.Background");
        var heading = new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10), FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "P.Ui.Foreground");
        _body.Children.Add(heading);
        if (!string.IsNullOrEmpty(message))
        {
            var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20 };
            text.SetResourceReference(TextBlock.ForegroundProperty, "P.Ui.Muted");
            _body.Children.Add(text);
        }
        var outer = new StackPanel();
        outer.Children.Add(_body);
        outer.Children.Add(_buttons);
        root.Child = outer;
        Content = root;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
            var th = ThemeManager.Instance.Current;
            Backdrop.Apply(hwnd, BackdropKind.Acrylic, ThemeManager.ParseColor(th.Dark ? "#202020" : "#F3F3F3", Colors.Gray), th.Dark, CornerKind.Round, null);
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { _result = null; Close(); } };
    }

    Button AddButton(string text, string? result, bool accent = false, bool isDefault = false)
    {
        var b = new Button { Content = text, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), IsDefault = isDefault };
        b.SetResourceReference(StyleProperty, accent ? "P.AccentButton" : "P.Button");
        b.Click += (_, _) => { _result = result; Close(); };
        _buttons.Children.Add(b);
        return b;
    }

    public static bool Confirm(string title, string message, string ok = "OK", string cancel = "Abbrechen")
    {
        var d = new Dialog(title, message);
        d.AddButton(cancel, null);
        d.AddButton(ok, "ok", accent: true, isDefault: true).Focus();
        d.ShowDialog();
        return d._result == "ok";
    }

    /// <summary>Drei Wege: gibt "a", "b" oder null (Abbrechen) zurück.</summary>
    public static string? Choose(string title, string message, string a, string b, string cancel = "Abbrechen")
    {
        var d = new Dialog(title, message);
        d.AddButton(cancel, null);
        d.AddButton(b, "b");
        d.AddButton(a, "a", accent: true, isDefault: true);
        d.ShowDialog();
        return d._result;
    }

    public static (bool Ok, bool Checked) ConfirmWithOption(string title, string message, string option, bool optionDefault, string ok)
    {
        var d = new Dialog(title, message);
        d._check = new CheckBox { Content = option, IsChecked = optionDefault, Margin = new Thickness(0, 16, 0, 0) };
        d._check.SetResourceReference(StyleProperty, "P.Toggle");
        d._body.Children.Add(d._check);
        d.AddButton("Abbrechen", null);
        d.AddButton(ok, "ok", accent: true, isDefault: true);
        d.ShowDialog();
        return (d._result == "ok", d._check.IsChecked == true);
    }

    public static void Info(string title, string message)
    {
        var d = new Dialog(title, message);
        d.AddButton("OK", "ok", accent: true, isDefault: true);
        d.ShowDialog();
    }

    public static string? Prompt(string title, string label, string value, bool selectBaseName = false)
    {
        var d = new Dialog(title, label);
        d._input = new TextBox { Text = value, Margin = new Thickness(0, 12, 0, 0) };
        d._input.SetResourceReference(StyleProperty, "P.TextBox");
        d._body.Children.Add(d._input);
        d.AddButton("Abbrechen", null);
        d.AddButton("Übernehmen", "ok", accent: true, isDefault: true);
        d.Loaded += (_, _) =>
        {
            d._input.Focus();
            int dot = value.LastIndexOf('.');
            if (selectBaseName && dot > 0) d._input.Select(0, dot); else d._input.SelectAll();
        };
        d.ShowDialog();
        return d._result == "ok" ? d._input.Text.Trim() : null;
    }

    public static string? PickFolder(string? initial = null)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Ordner für das Portal wählen" };
        if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial)) dlg.InitialDirectory = initial;
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }
}
