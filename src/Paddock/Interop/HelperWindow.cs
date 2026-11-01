using System.Windows.Interop;
using static Paddock.Interop.NativeMethods;

namespace Paddock.Interop;

/// <summary>
/// Unsichtbares Hilfsfenster: empfängt Tray-Nachrichten, leitet Menü-Nachrichten an
/// Shell-Kontextmenüs weiter (Untermenüs wie „Senden an“) und erkennt Explorer-Neustarts.
/// </summary>
public sealed class HelperWindow : IDisposable
{
    public const string Title = "Paddock.HelperWindow";
    public const int WM_TRAY = WM_APP + 1;
    public const int WM_ACTIVATE_INSTANCE = WM_APP + 2;

    readonly HwndSource _source;
    readonly int _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

    public IntPtr Handle => _source.Handle;
    public IContextMenu2? ActiveMenu2 { get; set; }
    public IContextMenu3? ActiveMenu3 { get; set; }

    public event Action<int>? TrayMessage;
    public event Action? ExplorerRestarted;
    public event Action? ActivateRequested;
    public event Action? DisplayChanged;

    public HelperWindow()
    {
        var p = new HwndSourceParameters(Title)
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            ExtendedWindowStyle = (int)WS_EX_TOOLWINDOW,
            Width = 0, Height = 0, PositionX = -32000, PositionY = -32000,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        // Owner-Draw-Menüs der Shell (Symbole, „Senden an“, „Öffnen mit“)
        if (msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)
        {
            if (ActiveMenu3 is not null && ActiveMenu3.HandleMenuMsg2(msg, w, l, out var r) == 0) { handled = true; return r; }
            if (ActiveMenu2 is not null && ActiveMenu2.HandleMenuMsg(msg, w, l) == 0) { handled = true; return IntPtr.Zero; }
        }

        if (msg == WM_TRAY) { TrayMessage?.Invoke(l.ToInt32() & 0xFFFF); handled = true; }
        else if (msg == WM_ACTIVATE_INSTANCE) { ActivateRequested?.Invoke(); handled = true; }
        else if (msg == WM_DISPLAYCHANGE) DisplayChanged?.Invoke();
        else if (msg == _taskbarCreated && _taskbarCreated != 0) { DesktopShell.Invalidate(); ExplorerRestarted?.Invoke(); }
        return IntPtr.Zero;
    }

    public void Dispose() => _source.Dispose();
}
