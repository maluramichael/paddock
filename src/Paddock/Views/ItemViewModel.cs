using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Microsoft.Win32;
using Paddock.Sdk;

namespace Paddock.Views;

public sealed class ItemViewModel : INotifyPropertyChanged
{
    public ItemViewModel(FileSystemInfo info)
    {
        Path = info.FullName;
        IsDirectory = info is DirectoryInfo;
        Modified = info.LastWriteTime;
        IsGhost = info.Attributes.HasFlag(FileAttributes.Hidden);
        Name = DisplayNames.For(info);
    }

    public string Path { get; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Name { get; }
    public bool IsDirectory { get; }
    public DateTime Modified { get; }
    public bool IsGhost { get; }
    public string Extension => IsDirectory ? "" : System.IO.Path.GetExtension(Path);
    public string ToolTip => $"{Name}\nGeändert: {Modified:g}";
    public ItemInfo Info => ItemInfo.From(Path);

    ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set { _icon = value; Raise(); } }
    public int IconPx { get; set; }

    bool _selected;
    public bool IsSelected { get => _selected; set { if (_selected == value) return; _selected = value; Raise(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Anzeigenamen wie im Explorer: ".lnk/.url" nie, bekannte Endungen je nach Explorer-Einstellung.</summary>
public static class DisplayNames
{
    static readonly Dictionary<string, bool> Registered = new(StringComparer.OrdinalIgnoreCase);
    static bool? _hideKnown;

    public static void Reset() { _hideKnown = null; Registered.Clear(); }

    static bool HideKnown => _hideKnown ??= (int)(Registry.GetValue(
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", 1) ?? 1) == 1;

    public static string For(FileSystemInfo info)
    {
        if (info is DirectoryInfo) return info.Name;
        var ext = info.Extension;
        if (ext.Length == 0) return info.Name;
        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".appref-ms", StringComparison.OrdinalIgnoreCase))
            return System.IO.Path.GetFileNameWithoutExtension(info.Name);
        if (!HideKnown) return info.Name;
        if (!Registered.TryGetValue(ext, out var known))
        {
            using var key = Registry.ClassesRoot.OpenSubKey(ext);
            Registered[ext] = known = key is not null;
        }
        return known ? System.IO.Path.GetFileNameWithoutExtension(info.Name) : info.Name;
    }
}
