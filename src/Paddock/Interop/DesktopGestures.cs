using System.Runtime.InteropServices;
using System.Windows.Threading;
using static Paddock.Interop.NativeMethods;

namespace Paddock.Interop;

/// <summary>
/// Maus-Gesten auf dem freien Desktop über einen Low-Level-Hook:
/// Doppelklick (Schnell-Ausblenden) und Rechtsklick-Ziehen (neuen Bereich zeichnen).
/// Der Hook arbeitet rein ereignisgesteuert und kehrt sofort zurück – kein Polling.
/// </summary>
public sealed class DesktopGestures : IDisposable
{
    readonly Dispatcher _ui;
    readonly LowLevelMouseProc _proc; // Referenz halten, sonst sammelt der GC den Delegate ein
    IntPtr _hook;

    uint _lastClickTime;
    POINT _lastClickPt;

    enum RState { Idle, Pending, Drawing }
    RState _r = RState.Idle;
    POINT _rStart;

    static readonly bool Debug = Environment.GetEnvironmentVariable("PADDOCK_DEBUG") == "1";
    static readonly IntPtr OwnMarker = new(0x50414444); // "PADD"

    public bool DoubleClickEnabled { get; set; }
    public bool DrawEnabled { get; set; }

    public event Action? DesktopDoubleClicked;
    public event Action<RECT>? DrawStarted;
    public event Action<RECT>? DrawUpdated;
    public event Action<RECT>? DrawCompleted;

    public DesktopGestures(Dispatcher ui)
    {
        _ui = ui;
        _proc = Hook;
    }

    /// <summary>Installiert/entfernt den Hook je nach Bedarf – ohne aktive Gesten läuft kein Hook.</summary>
    public void Update()
    {
        bool need = DoubleClickEnabled || DrawEnabled;
        if (need && _hook == IntPtr.Zero)
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        else if (!need && _hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(_hook, code, wParam, lParam);
        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        int msg = wParam.ToInt32();
        // Nur unsere eigenen nachgereichten Klicks ignorieren (markiert über dwExtraInfo)
        bool injected = (info.flags & LLMHF_INJECTED) != 0 && info.dwExtraInfo == OwnMarker;

        if (Debug && msg == WM_RBUTTONUP) Core.Log.Info($"RUP state={_r} injected={injected} flags={info.flags}");
        if (Debug && msg == WM_MOUSEMOVE && _r != RState.Idle) Core.Log.Info($"MOVE {info.pt.X},{info.pt.Y} state={_r}");
        try
        {
            switch (msg)
            {
                case WM_LBUTTONDOWN when DoubleClickEnabled && !injected:
                    HandleLeftDown(info);
                    break;

                case WM_RBUTTONDOWN when DrawEnabled && !injected && _r == RState.Idle:
                    if (Debug) Core.Log.Info($"RDOWN {info.pt.X},{info.pt.Y} desktop={DesktopShell.IsDesktopAt(info.pt)} empty={DesktopShell.IsEmptyDesktopAt(info.pt)} cls={GetClassName(WindowFromPoint(info.pt))}");
                    if (DesktopShell.IsEmptyDesktopAt(info.pt))
                    {
                        _r = RState.Pending;
                        _rStart = info.pt;
                        return new IntPtr(1); // schlucken – Kontextmenü erst, wenn es kein Ziehen wird
                    }
                    break;

                case WM_MOUSEMOVE when _r != RState.Idle:
                    if (_r == RState.Pending &&
                        (Math.Abs(info.pt.X - _rStart.X) > GetSystemMetrics(SM_CXDRAG) * 2 ||
                         Math.Abs(info.pt.Y - _rStart.Y) > GetSystemMetrics(SM_CYDRAG) * 2))
                    {
                        _r = RState.Drawing;
                        var r0 = Rect(_rStart, info.pt);
                        _ui.BeginInvoke(() => DrawStarted?.Invoke(r0));
                    }
                    else if (_r == RState.Drawing)
                    {
                        var r1 = Rect(_rStart, info.pt);
                        _ui.BeginInvoke(DispatcherPriority.Render, () => DrawUpdated?.Invoke(r1));
                    }
                    break;

                case WM_RBUTTONUP when !injected && _r != RState.Idle:
                    var state = _r;
                    _r = RState.Idle;
                    if (state == RState.Drawing)
                    {
                        var r2 = Rect(_rStart, info.pt);
                        _ui.BeginInvoke(() => DrawCompleted?.Invoke(r2));
                    }
                    else
                    {
                        // War nur ein Klick → das normale Desktop-Kontextmenü nachreichen
                        _ui.BeginInvoke(SendRightClick);
                    }
                    return new IntPtr(1);
            }
        }
        catch { /* niemals im Hook werfen */ }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    void HandleLeftDown(MSLLHOOKSTRUCT info)
    {
        uint dt = info.time - _lastClickTime;
        bool isDouble = dt <= GetDoubleClickTime()
                        && Math.Abs(info.pt.X - _lastClickPt.X) <= GetSystemMetrics(SM_CXDOUBLECLK)
                        && Math.Abs(info.pt.Y - _lastClickPt.Y) <= GetSystemMetrics(SM_CYDOUBLECLK);
        if (isDouble)
        {
            _lastClickTime = 0;
            var pt = info.pt;
            // Treffer-Test außerhalb des Hooks, damit der Hook sofort zurückkehrt
            _ui.BeginInvoke(() => { if (DesktopShell.IsEmptyDesktopAt(pt)) DesktopDoubleClicked?.Invoke(); });
        }
        else
        {
            _lastClickTime = info.time;
            _lastClickPt = info.pt;
        }
    }

    static void SendRightClick()
    {
        var inputs = new[]
        {
            new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_RIGHTDOWN, dwExtraInfo = OwnMarker } },
            new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_RIGHTUP, dwExtraInfo = OwnMarker } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    static RECT Rect(POINT a, POINT b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
