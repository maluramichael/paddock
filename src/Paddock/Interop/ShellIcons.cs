using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Paddock.Core;

namespace Paddock.Interop;

/// <summary>
/// Lädt Symbole/Vorschaubilder über IShellItemImageFactory auf einem eigenen STA-Thread,
/// mit kleinem LRU-Cache. Ergebnisse werden eingefroren und mit niedriger Priorität zugestellt.
/// </summary>
public sealed class ShellIcons
{
    public static ShellIcons Instance { get; } = new();

    const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;
    const int CacheLimit = 400;

    readonly BlockingCollection<Request> _queue = new();
    readonly Dictionary<string, LinkedListNode<(string Key, ImageSource Image)>> _cache = new();
    readonly LinkedList<(string Key, ImageSource Image)> _lru = new();
    readonly Dispatcher _ui;

    record Request(string Path, int Px, bool Thumbnail, string Key, Action<ImageSource> Callback);

    ShellIcons()
    {
        _ui = Application.Current.Dispatcher;
        var t = new Thread(Worker) { IsBackground = true, Name = "Paddock.Icons", Priority = ThreadPriority.BelowNormal };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>Gibt ein Bild sofort aus dem Cache zurück oder lädt es asynchron und ruft den Callback (UI-Thread).</summary>
    public ImageSource? Get(string path, int px, bool thumbnail, Action<ImageSource> callback)
    {
        long stamp = 0;
        try { stamp = File.GetLastWriteTimeUtc(path).Ticks; } catch { }
        var key = $"{path}|{px}|{thumbnail}|{stamp}";
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _lru.Remove(node); _lru.AddFirst(node);
                return node.Value.Image;
            }
        }
        _queue.Add(new Request(path, px, thumbnail, key, callback));
        return null;
    }

    public void Clear() { lock (_cache) { _cache.Clear(); _lru.Clear(); } }

    void Worker()
    {
        foreach (var req in _queue.GetConsumingEnumerable())
        {
            try
            {
                var img = Load(req.Path, req.Px, req.Thumbnail);
                if (img is null) continue;
                lock (_cache)
                {
                    if (!_cache.ContainsKey(req.Key))
                    {
                        _cache[req.Key] = _lru.AddFirst((req.Key, img));
                        while (_lru.Count > CacheLimit) { _cache.Remove(_lru.Last!.Value.Key); _lru.RemoveLast(); }
                    }
                }
                _ui.BeginInvoke(DispatcherPriority.Background, () => req.Callback(img));
            }
            catch (Exception ex) { Log.Error($"Symbol für {req.Path}", ex); }
        }
    }

    static ImageSource? Load(string path, int px, bool thumbnail)
    {
        var iid = ShellGuids.IShellItemImageFactory;
        IShellItemImageFactory? factory = null;
        try
        {
            NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
            int flags = SIIGBF_BIGGERSIZEOK | (thumbnail ? 0 : SIIGBF_ICONONLY);
            if (factory.GetImage(new SIZE(px, px), flags, out var hbmp) != 0 || hbmp == IntPtr.Zero) return null;
            try { return ToBitmapSource(hbmp); }
            finally { NativeMethods.DeleteObject(hbmp); }
        }
        catch { return null; }
        finally { if (factory is not null) Marshal.ReleaseComObject(factory); }
    }

    /// <summary>HBITMAP (32 bpp, vormultipliertes Alpha) → eingefrorenes BitmapSource ohne Alpha-Verlust.</summary>
    static BitmapSource? ToBitmapSource(IntPtr hbmp)
    {
        if (NativeMethods.GetObject(hbmp, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
        };
        var bits = new byte[w * h * 4];
        var hdc = NativeMethods.GetDC(IntPtr.Zero);
        try { if (NativeMethods.GetDIBits(hdc, hbmp, 0, (uint)h, bits, ref bmi, 0) == 0) return null; }
        finally { NativeMethods.ReleaseDC(IntPtr.Zero, hdc); }

        // Manche Vorschaubilder haben gar keinen Alphakanal → wären sonst unsichtbar
        bool anyAlpha = false;
        for (int i = 3; i < bits.Length; i += 4) if (bits[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < bits.Length; i += 4) bits[i] = 255;

        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, bits, w * 4);
        src.Freeze();
        return src;
    }
}
