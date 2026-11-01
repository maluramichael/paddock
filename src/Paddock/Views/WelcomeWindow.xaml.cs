using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Paddock.Core;
using Paddock.Interop;

namespace Paddock.Views;

public partial class WelcomeWindow : Window
{
    public WelcomeWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
            var th = ThemeManager.Instance.Current;
            Backdrop.Apply(hwnd, BackdropKind.Acrylic, ThemeManager.ParseColor(th.Dark ? "#202020" : "#F3F3F3", Colors.Gray), th.Dark, CornerKind.Round, null);
        };
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    void Later_Click(object sender, RoutedEventArgs e) => Close();

    void Empty_Click(object sender, RoutedEventArgs e)
    {
        Close();
        App.Host.Fences.CreateBeside(null, FenceKind.Collection);
    }

    void Organize_Click(object sender, RoutedEventArgs e)
    {
        Close();
        App.Host.Fences.OrganizeDesktop();
    }
}
