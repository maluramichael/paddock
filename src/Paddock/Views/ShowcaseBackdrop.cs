using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Controls;

namespace Paddock.Views;

/// <summary>
/// Nur für Screenshots (PADDOCK_SHOWCASE=1): ein neutraler Farbverlauf als „Hintergrundbild“
/// über dem echten Desktop, damit Bereiche ohne private Inhalte fotografiert werden können.
/// </summary>
public static class ShowcaseBackdrop
{
    public static void ShowFor(Application app)
    {
        var canvas = new Canvas
        {
            Background = new LinearGradientBrush(
                [new GradientStop(Color.FromRgb(0x14, 0x1B, 0x3D), 0), new GradientStop(Color.FromRgb(0x2E, 0x1F, 0x5E), 0.45),
                 new GradientStop(Color.FromRgb(0x0E, 0x4D, 0x64), 1)], new Point(0, 0), new Point(1, 1)),
        };
        void Blob(double x, double y, double size, Color c)
        {
            var e = new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(c), Effect = new BlurEffect { Radius = size / 3 } };
            Canvas.SetLeft(e, x); Canvas.SetTop(e, y);
            canvas.Children.Add(e);
        }
        var w = SystemParameters.PrimaryScreenWidth; var h = SystemParameters.PrimaryScreenHeight;
        Blob(w * 0.05, h * 0.10, w * 0.30, Color.FromArgb(150, 0xFF, 0x7A, 0x59));
        Blob(w * 0.55, h * 0.05, w * 0.35, Color.FromArgb(140, 0x5B, 0x6C, 0xFF));
        Blob(w * 0.35, h * 0.55, w * 0.32, Color.FromArgb(130, 0x22, 0xC1, 0xC3));
        Blob(w * 0.75, h * 0.60, w * 0.22, Color.FromArgb(120, 0xF7, 0xB7, 0x31));

        var win = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
            Left = 0, Top = 0, Width = w, Height = h, Content = canvas, ShowActivated = false,
        };
        win.Show();
    }
}
