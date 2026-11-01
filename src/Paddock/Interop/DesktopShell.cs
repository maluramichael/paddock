using System.Runtime.InteropServices;
using System.Text;
using static Paddock.Interop.NativeMethods;

namespace Paddock.Interop;

/// <summary>
/// Zugriff auf den Explorer-Desktop: Progman/WorkerW, die Symbol-Listview,
/// Treffer-Tests und das Ein-/Ausblenden der nativen Desktop-Symbole.
/// </summary>
public static class DesktopShell
{
    static IntPtr _defView, _listView;

    public static IntPtr Progman => FindWindow("Progman", null);

    public static void Invalidate() { _defView = IntPtr.Zero; _listView = IntPtr.Zero; }

    public static IntPtr DefView
    {
        get
        {
            if (_defView != IntPtr.Zero && IsWindow(_defView)) return _defView;
            var def = FindWindowEx(Progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (def == IntPtr.Zero)
            {
                EnumWindows((h, _) =>
                {
                    if (GetClassName(h) != "WorkerW") return true;
                    var d = FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (d == IntPtr.Zero) return true;
                    def = d;
                    return false;
                }, IntPtr.Zero);
            }
            return _defView = def;
        }
    }

    public static IntPtr ListView
    {
        get
        {
            if (_listView != IntPtr.Zero && IsWindow(_listView)) return _listView;
            var def = DefView;
            return _listView = def == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(def, IntPtr.Zero, "SysListView32", null);
        }
    }

    public static bool NativeIconsVisible
    {
        get { var lv = ListView; return lv != IntPtr.Zero && IsWindowVisible(lv); }
    }

    /// <summary>Entspricht „Ansicht → Desktopsymbole anzeigen“ im Desktop-Kontextmenü.</summary>
    public static void SetNativeIconsVisible(bool visible)
    {
        if (NativeIconsVisible == visible) return;
        var def = DefView;
        if (def != IntPtr.Zero) SendMessage(def, WM_COMMAND, new IntPtr(0x7402), IntPtr.Zero);
    }

    /// <summary>Liegt das Fenster unter dem Punkt zum Desktop (Symbol-Listview, Hintergrund)?</summary>
    public static bool IsDesktopAt(POINT pt)
    {
        var hwnd = WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return false;
        var cls = GetClassName(hwnd);
        if (cls is "SysListView32" or "SHELLDLL_DefView")
        {
            var root = GetClassName(GetAncestor(hwnd, GA_ROOT));
            return root is "Progman" or "WorkerW";
        }
        return cls is "Progman" or "WorkerW";
    }

    /// <summary>Freie Desktop-Fläche (kein Symbol) unter dem Punkt?</summary>
    public static bool IsEmptyDesktopAt(POINT pt)
    {
        if (!IsDesktopAt(pt)) return false;
        var lv = ListView;
        if (lv == IntPtr.Zero || !IsWindowVisible(lv)) return true;
        var client = pt;
        ScreenToClient(lv, ref client);
        return HitTest(lv, client) < 0;
    }

    static int HitTest(IntPtr lv, POINT client)
    {
        using var mem = RemoteMemory.For(lv, 32);
        if (mem is null) return -1;
        var buf = new byte[24];
        BitConverter.GetBytes(client.X).CopyTo(buf, 0);
        BitConverter.GetBytes(client.Y).CopyTo(buf, 4);
        mem.Write(buf);
        SendMessageTimeout(lv, LVM_HITTEST, IntPtr.Zero, mem.Address, SMTO_ABORTIFHUNG, 100, out var result);
        return result.ToInt32();
    }

    public sealed record DesktopIcon(string Label, RECT ScreenBounds);

    /// <summary>Liest Beschriftung und Bildschirm-Rechteck aller nativen Desktop-Symbole.</summary>
    public static List<DesktopIcon> GetIcons()
    {
        var result = new List<DesktopIcon>();
        var lv = ListView;
        if (lv == IntPtr.Zero || !IsWindowVisible(lv)) return result;
        SendMessageTimeout(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 200, out var countPtr);
        int count = countPtr.ToInt32();
        if (count <= 0) return result;

        const int lvItemSize = 88, textChars = 260;
        using var mem = RemoteMemory.For(lv, lvItemSize + textChars * 2 + 16);
        if (mem is null) return result;
        var origin = new POINT(0, 0);
        ClientToScreen(lv, ref origin);

        for (int i = 0; i < count; i++)
        {
            // Rechteck (LVIR_BOUNDS = 0 in left)
            mem.Write(new byte[16]);
            SendMessageTimeout(lv, LVM_GETITEMRECT, new IntPtr(i), mem.Address, SMTO_ABORTIFHUNG, 100, out _);
            var rb = mem.Read(16);
            var rect = new RECT(BitConverter.ToInt32(rb, 0) + origin.X, BitConverter.ToInt32(rb, 4) + origin.Y,
                                BitConverter.ToInt32(rb, 8) + origin.X, BitConverter.ToInt32(rb, 12) + origin.Y);

            // Text: LVITEMW mit pszText auf Puffer direkt dahinter
            var item = new byte[lvItemSize];
            BitConverter.GetBytes(0x1).CopyTo(item, 0);                 // mask = LVIF_TEXT
            BitConverter.GetBytes(i).CopyTo(item, 4);
            BitConverter.GetBytes((long)(mem.Address + lvItemSize)).CopyTo(item, 24);
            BitConverter.GetBytes(textChars).CopyTo(item, 32);
            mem.Write(item);
            SendMessageTimeout(lv, LVM_GETITEMTEXTW, new IntPtr(i), mem.Address, SMTO_ABORTIFHUNG, 100, out var len);
            var tb = mem.Read(textChars * 2, lvItemSize);
            var label = Encoding.Unicode.GetString(tb, 0, Math.Clamp(len.ToInt32(), 0, textChars) * 2);
            result.Add(new DesktopIcon(label, rect));
        }
        return result;
    }

    /// <summary>Kleiner Helfer für Speicher im Explorer-Prozess (für Listview-Nachrichten).</summary>
    sealed class RemoteMemory : IDisposable
    {
        readonly IntPtr _proc;
        public IntPtr Address { get; }
        readonly int _size;

        RemoteMemory(IntPtr proc, IntPtr addr, int size) { _proc = proc; Address = addr; _size = size; }

        public static RemoteMemory? For(IntPtr hwnd, int size)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            var proc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
            if (proc == IntPtr.Zero) return null;
            var addr = VirtualAllocEx(proc, IntPtr.Zero, (UIntPtr)size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (addr == IntPtr.Zero) { CloseHandle(proc); return null; }
            return new RemoteMemory(proc, addr, size);
        }

        public void Write(byte[] data, int offset = 0) =>
            WriteProcessMemory(_proc, Address + offset, data, (UIntPtr)Math.Min(data.Length, _size - offset), out _);

        public byte[] Read(int count, int offset = 0)
        {
            var buf = new byte[count];
            ReadProcessMemory(_proc, Address + offset, buf, (UIntPtr)count, out _);
            return buf;
        }

        public void Dispose()
        {
            VirtualFreeEx(_proc, Address, UIntPtr.Zero, MEM_RELEASE);
            CloseHandle(_proc);
        }
    }

    /// <summary>Anzeigename einer Datei, so wie Explorer ihn zeigt (z. B. ohne ".lnk").</summary>
    public static string DisplayName(string path)
    {
        var info = new SHFILEINFO();
        SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_DISPLAYNAME);
        return string.IsNullOrEmpty(info.szDisplayName) ? Path.GetFileName(path) : info.szDisplayName;
    }

    public static string TypeName(string path)
    {
        var info = new SHFILEINFO();
        SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_TYPENAME);
        return info.szTypeName ?? "";
    }

    public static string UserDesktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public static string PublicDesktop => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
}
