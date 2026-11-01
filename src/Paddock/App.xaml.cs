using System.Windows;
using Paddock.Core;
using Paddock.Interop;

namespace Paddock;

public partial class App : Application
{
    public static AppHost Host { get; private set; } = null!;

    /// <summary>Vorführmodus für Screenshots: Bereiche liegen über einem neutralen Hintergrund statt auf dem Desktop.</summary>
    public static bool Showcase { get; } = Environment.GetEnvironmentVariable("PADDOCK_SHOWCASE") == "1";
    Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Nur eine Instanz: eine zweite öffnet stattdessen die Einstellungen der ersten
        _mutex = new Mutex(true, @"Local\Paddock.SingleInstance", out bool first);
        if (!first)
        {
            var other = NativeMethods.FindWindow(null, HelperWindow.Title);
            if (other != IntPtr.Zero) NativeMethods.PostMessage(other, HelperWindow.WM_ACTIVATE_INSTANCE, IntPtr.Zero, IntPtr.Zero);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unbehandelter Fehler", ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Absturz", ex.ExceptionObject as Exception);

        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Paddock;component/Views/Styles.xaml") });
        if (Showcase) Views.ShowcaseBackdrop.ShowFor(this);
        Host = new AppHost();
        Host.Start(e.Args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Host?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
