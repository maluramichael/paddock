using System.Runtime.InteropServices;
using static Paddock.Interop.NativeMethods;

namespace Paddock.Interop;

/// <summary>Schlankes Infobereich-Symbol direkt über Shell_NotifyIcon (ohne WinForms).</summary>
public sealed class TrayIcon : IDisposable
{
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    public const int WM_LBUTTONUP_ = 0x202, WM_RBUTTONUP_ = 0x205, WM_LBUTTONDBLCLK_ = 0x203, WM_CONTEXTMENU_ = 0x7B, NIN_SELECT = 0x400;

    readonly HelperWindow _helper;
    IntPtr _icon;
    string _tip;

    public TrayIcon(HelperWindow helper, string tip)
    {
        _helper = helper;
        _tip = tip;
        _icon = LoadAppIcon();
        Add();
    }

    static IntPtr LoadAppIcon()
    {
        int size = GetSystemMetrics(49); // SM_CXSMICON (DPI-abhängig über Manifest)
        var icons = new IntPtr[1];
        var path = Environment.ProcessPath ?? "";
        if (PrivateExtractIcons(path, 0, size, size, icons, null, 1, 0) > 0 && icons[0] != IntPtr.Zero) return icons[0];
        return IntPtr.Zero;
    }

    NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _helper.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = HelperWindow.WM_TRAY,
        hIcon = _icon,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    public void Add()
    {
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_ADD, ref d);
        d.uVersion = 4;
        Shell_NotifyIcon(NIM_SETVERSION, ref d);
    }

    public void SetTip(string tip)
    {
        _tip = tip;
        var d = Data(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    public void ShowBalloon(string title, string text)
    {
        var d = Data(NIF_INFO);
        d.szInfoTitle = title.Length > 63 ? title[..63] : title;
        d.szInfo = text.Length > 255 ? text[..255] : text;
        d.dwInfoFlags = 0x4 | 0x20; // NIIF_USER | NIIF_LARGE_ICON
        d.hBalloonIcon = _icon;
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    public void Dispose()
    {
        var d = Data(0);
        Shell_NotifyIcon(NIM_DELETE, ref d);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }
}
