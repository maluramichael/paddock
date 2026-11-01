using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Paddock.Core;
using Paddock.Interop;
using static Paddock.Interop.NativeMethods;

namespace Paddock.Views;

/// <summary>Live-Vorschau beim Aufziehen eines neuen Bereichs mit der rechten Maustaste.</summary>
public sealed class DrawPreview : Window
{
    IntPtr _hwnd;
    readonly TextBlock _label;

    public DrawPreview()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Transparent;
        Width = 1; Height = 1;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(0), ResizeBorderThickness = new Thickness(0), UseAeroCaptionButtons = false });

        var border = new Border { BorderThickness = new Thickness(2) };
        border.SetResourceReference(Border.BorderBrushProperty, "P.Accent");
        border.SetResourceReference(Border.BackgroundProperty, "P.Tint");
        border.SetResourceReference(Border.CornerRadiusProperty, "P.CornerRadius");
        _label = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, TextAlignment = TextAlignment.Center };
        _label.SetResourceReference(TextBlock.ForegroundProperty, "P.Title.Foreground");
        _label.SetResourceReference(TextBlock.FontFamilyProperty, "P.FontFamily");
        border.Child = _label;
        Content = border;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
        SetExStyle(_hwnd, GetExStyle(_hwnd) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        var th = ThemeManager.Instance.Current;
        Backdrop.Apply(_hwnd, th.Backdrop, ThemeManager.ParseColor(th.Tint, Colors.Black), th.Dark, th.Corners, null);
    }

    public void Place(RECT r)
    {
        if (_hwnd == IntPtr.Zero) { Show(); }
        SetWindowPos(_hwnd, HWND_TOPMOST, r.Left, r.Top, Math.Max(r.Width, 2), Math.Max(r.Height, 2), SWP_NOACTIVATE | SWP_SHOWWINDOW);
        double s = DpiFor(_hwnd) / 96.0;
        _label.Text = r.Width > 120 * s && r.Height > 50 * s ? "Neuer Bereich\nLoslassen zum Erstellen" : "";
    }
}
