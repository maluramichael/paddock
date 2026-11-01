using System.Runtime.InteropServices;
using Paddock.Core;
using static Paddock.Interop.NativeMethods;

namespace Paddock.Interop;

/// <summary>
/// Native Explorer-Funktionen für Dateien eines Ordners: Kontextmenü, Ziehen (mit Shell-Vorschau),
/// Öffnen sowie Dateioperationen mit Explorer-Dialogen und Rückgängig-Unterstützung.
/// </summary>
public static class ShellItems
{
    const uint CMF_NORMAL = 0, CMF_EXPLORE = 0x4, CMF_CANRENAME = 0x10, CMF_EXTENDEDVERBS = 0x100;
    const uint CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000, CMIC_MASK_SHIFT_DOWN = 0x10000000, CMIC_MASK_CONTROL_DOWN = 0x40000000;
    const uint GCS_VERBW = 4;
    const int FirstShellId = 1000;

    public sealed record MenuEntry(string Text, Action Action, bool Enabled = true, bool Separator = false)
    {
        public static MenuEntry Divider => new("", () => { }, true, true);
    }

    /// <summary>Hält PIDLs + IShellFolder für eine Auswahl von Dateien desselben Ordners.</summary>
    sealed class Selection : IDisposable
    {
        public IShellFolder Folder = null!;
        public IntPtr[] Children = [];
        readonly List<IntPtr> _absolute = [];

        public static Selection? Create(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0) return null;
            var sel = new Selection();
            try
            {
                foreach (var p in paths)
                {
                    if (SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero) continue;
                    sel._absolute.Add(pidl);
                }
                if (sel._absolute.Count == 0) { sel.Dispose(); return null; }
                var iid = ShellGuids.IShellFolder;
                if (SHBindToParent(sel._absolute[0], ref iid, out var folderPtr, out _) != 0) { sel.Dispose(); return null; }
                sel.Folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
                Marshal.Release(folderPtr);
                sel.Children = sel._absolute.Select(ILFindLastID).ToArray();
                return sel;
            }
            catch { sel.Dispose(); return null; }
        }

        public IntPtr GetUIObject(IntPtr hwnd, Guid iid)
        {
            return Folder.GetUIObjectOf(hwnd, (uint)Children.Length, Children, ref iid, IntPtr.Zero, out var ppv) == 0 ? ppv : IntPtr.Zero;
        }

        public void Dispose()
        {
            foreach (var p in _absolute) ILFree(p);
            if (Folder is not null) Marshal.ReleaseComObject(Folder);
        }
    }

    /// <summary>
    /// Zeigt das echte Explorer-Kontextmenü. Eigene Einträge stehen oben.
    /// „Umbenennen“ wird abgefangen und an <paramref name="onRename"/> gegeben.
    /// </summary>
    public static void ShowContextMenu(HelperWindow helper, IReadOnlyList<string> paths, POINT screen,
        IReadOnlyList<MenuEntry> extra, Action<string>? onRename, bool extended)
    {
        using var sel = Selection.Create(paths);
        if (sel is null) return;
        var cmPtr = sel.GetUIObject(helper.Handle, ShellGuids.IContextMenu);
        if (cmPtr == IntPtr.Zero) return;
        var cm = (IContextMenu)Marshal.GetObjectForIUnknown(cmPtr);
        Marshal.Release(cmPtr);
        var menu = CreatePopupMenu();
        try
        {
            for (int i = 0; i < extra.Count; i++)
            {
                var e = extra[i];
                if (e.Separator) AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                else AppendMenu(menu, MF_STRING | (e.Enabled ? 0 : MF_GRAYED), (UIntPtr)(i + 1), e.Text);
            }
            if (extra.Count > 0) AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);

            uint flags = CMF_NORMAL | CMF_EXPLORE | (onRename is not null ? CMF_CANRENAME : 0) | (extended ? CMF_EXTENDEDVERBS : 0);
            cm.QueryContextMenu(menu, (uint)GetMenuItemCount(menu), FirstShellId, 0x7FFF, flags);

            helper.ActiveMenu2 = cm as IContextMenu2;
            helper.ActiveMenu3 = cm as IContextMenu3;
            SetForegroundWindow(helper.Handle);
            var cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, screen.X, screen.Y, helper.Handle, IntPtr.Zero);
            PostMessage(helper.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            helper.ActiveMenu2 = null;
            helper.ActiveMenu3 = null;

            if (cmd == 0) return;
            if (cmd < FirstShellId) { extra[(int)cmd - 1].Action(); return; }

            var verb = GetVerb(cm, cmd - FirstShellId);
            if (string.Equals(verb, "rename", StringComparison.OrdinalIgnoreCase) && onRename is not null)
            {
                onRename(paths[0]);
                return;
            }
            Invoke(cm, helper.Handle, cmd - FirstShellId, screen);
        }
        catch (Exception ex) { Log.Error("Kontextmenü", ex); }
        finally
        {
            DestroyMenu(menu);
            Marshal.ReleaseComObject(cm);
        }
    }

    static string? GetVerb(IContextMenu cm, uint offset)
    {
        var buf = Marshal.AllocHGlobal(520);
        try
        {
            return cm.GetCommandString((UIntPtr)offset, GCS_VERBW, IntPtr.Zero, buf, 260) == 0 ? Marshal.PtrToStringUni(buf) : null;
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    static void Invoke(IContextMenu cm, IntPtr hwnd, uint offset, POINT pt)
    {
        var info = new CMINVOKECOMMANDINFOEX
        {
            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
            fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE
                    | (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? CMIC_MASK_SHIFT_DOWN : 0)
                    | (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) ? CMIC_MASK_CONTROL_DOWN : 0),
            hwnd = hwnd,
            lpVerb = (IntPtr)offset,
            lpVerbW = (IntPtr)offset,
            nShow = 1,
            ptInvoke = pt,
        };
        cm.InvokeCommand(ref info);
    }

    /// <summary>Startet einen Shell-Zieh-Vorgang (inkl. Vorschaubild). Blockiert bis zum Ablegen.</summary>
    public static int DoDragDrop(IntPtr hwnd, IReadOnlyList<string> paths)
    {
        using var sel = Selection.Create(paths);
        if (sel is null) return 0;
        var data = sel.GetUIObject(hwnd, ShellGuids.IDataObject);
        if (data == IntPtr.Zero) return 0;
        try
        {
            const int DROPEFFECT_COPY = 1, DROPEFFECT_MOVE = 2, DROPEFFECT_LINK = 4;
            SHDoDragDrop(hwnd, data, IntPtr.Zero, DROPEFFECT_COPY | DROPEFFECT_MOVE | DROPEFFECT_LINK, out var effect);
            return effect;
        }
        finally { Marshal.Release(data); }
    }

    /// <summary>Öffnet eine Datei wie ein Doppelklick im Explorer.</summary>
    public static void Open(string path, string? verb = null)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = 0x00100000, // SEE_MASK_ASYNCOK – nicht auf die Anwendung warten
            lpFile = path,
            lpVerb = verb,
            lpDirectory = Path.GetDirectoryName(path),
            nShow = 1,
        };
        if (ShellExecuteEx(ref info) == 0) Log.Info($"Öffnen fehlgeschlagen: {path}");
    }

    public static void ShowInExplorer(string path)
    {
        if (Directory.Exists(path)) Open(path);
        else System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    // --- Dateioperationen ---------------------------------------------------------------

    const uint FO_MOVE = 1, FO_COPY = 2, FO_DELETE = 3, FO_RENAME = 4;
    const ushort FOF_SILENT = 0x4, FOF_RENAMEONCOLLISION = 0x8, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40,
        FOF_NOCONFIRMMKDIR = 0x200, FOF_NOERRORUI = 0x400;

    /// <summary>Verschiebt Dateien mit Explorer-Dialogen bei Konflikten (Rückgängig mit Strg+Z im Explorer).</summary>
    public static bool Move(IntPtr hwnd, IReadOnlyList<string> sources, string targetDir, bool silent = false) =>
        Run(hwnd, FO_MOVE, sources, targetDir, (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR | (silent ? FOF_SILENT | FOF_NOERRORUI | FOF_RENAMEONCOLLISION | FOF_NOCONFIRMATION : 0)));

    public static bool Copy(IntPtr hwnd, IReadOnlyList<string> sources, string targetDir)
    {
        // Kopie in denselben Ordner → „Name - Kopie“
        bool same = sources.All(s => string.Equals(Path.GetDirectoryName(s), targetDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        return Run(hwnd, FO_COPY, sources, targetDir, (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR | (same ? FOF_RENAMEONCOLLISION : 0)));
    }

    public static bool Rename(IntPtr hwnd, string source, string newPath) =>
        Run(hwnd, FO_RENAME, [source], newPath, FOF_ALLOWUNDO);

    public static bool Recycle(IntPtr hwnd, IReadOnlyList<string> sources) =>
        Run(hwnd, FO_DELETE, sources, null, FOF_ALLOWUNDO);

    static bool Run(IntPtr hwnd, uint func, IReadOnlyList<string> sources, string? target, ushort flags)
    {
        if (sources.Count == 0) return false;
        var op = new SHFILEOPSTRUCT
        {
            hwnd = hwnd,
            wFunc = func,
            pFrom = string.Join('\0', sources) + "\0\0",
            pTo = target is null ? null : target + "\0\0",
            fFlags = flags,
        };
        int rc = SHFileOperation(ref op);
        if (rc != 0) Log.Info($"Dateioperation {func} → 0x{rc:X}");
        return rc == 0 && !op.fAnyOperationsAborted;
    }

    /// <summary>Erzeugt eine Verknüpfung (.lnk) über das Windows Script Host-Objekt.</summary>
    public static void CreateShortcut(string target, string dir)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(target.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name)) name = target.TrimEnd('\\');
            var lnk = Path.Combine(dir, name + ".lnk");
            int i = 2;
            while (File.Exists(lnk)) lnk = Path.Combine(dir, $"{name} ({i++}).lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = target;
            sc.WorkingDirectory = Path.GetDirectoryName(target) ?? "";
            sc.Save();
            Marshal.ReleaseComObject(sc);
            Marshal.ReleaseComObject(shell);
        }
        catch (Exception ex) { Log.Error("Verknüpfung", ex); }
    }
}
