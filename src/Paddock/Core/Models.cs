using System.Text.Json.Serialization;

namespace Paddock.Core;

public enum FenceKind
{
    /// <summary>Eigener Ordner unter dem Sammelordner; hineingezogene Desktop-Dateien werden dorthin verschoben.</summary>
    Collection,
    /// <summary>Zeigt einen beliebigen vorhandenen Ordner („Ordner-Portal“).</summary>
    Portal,
}

public enum FenceViewMode { Tiles, List }
public enum FenceSort { Manual, Name, Type, Modified }

public sealed class FenceConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "Neuer Bereich";
    public FenceKind Kind { get; set; } = FenceKind.Collection;
    public string FolderPath { get; set; } = "";

    // Physische Bildschirmkoordinaten (Pixel)
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 420;
    public int Height { get; set; } = 260;

    public bool Collapsed { get; set; }
    public bool Locked { get; set; }
    /// <summary>Eigene Tönung (#RRGGBB) statt der Theme-Farbe.</summary>
    public string? Tint { get; set; }
    public int IconSize { get; set; } = 48;
    public FenceViewMode ViewMode { get; set; } = FenceViewMode.Tiles;
    public FenceSort Sort { get; set; } = FenceSort.Manual;
    /// <summary>Dateinamen in manueller Reihenfolge.</summary>
    public List<string> Order { get; set; } = [];
    /// <summary>Doppelklick auf Unterordner navigiert innerhalb des Bereichs.</summary>
    public bool NavigateFolders { get; set; }
}

public sealed class RuleConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string ConditionId { get; set; } = "extension";
    public string Parameter { get; set; } = "";
    public Guid TargetFenceId { get; set; }
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string Theme { get; set; } = "mitternacht";

    /// <summary>Doppelklick auf freien Desktop blendet alles aus/ein.</summary>
    public bool QuickHide { get; set; } = true;
    /// <summary>Beim Ausblenden auch die nativen Desktop-Symbole verstecken.</summary>
    public bool QuickHideNativeIcons { get; set; } = true;
    /// <summary>Rechte Maustaste + Ziehen auf dem Desktop zeichnet einen neuen Bereich.</summary>
    public bool DrawToCreate { get; set; } = true;
    public bool SingleClickOpen { get; set; }
    public bool Snap { get; set; } = true;
    /// <summary>Eingeklappte Bereiche öffnen sich beim Darüberfahren.</summary>
    public bool PeekCollapsed { get; set; } = true;
    public bool ShowHiddenFiles { get; set; }
    public bool ShowThumbnails { get; set; } = true;
    public bool RulesEnabled { get; set; } = true;
    public bool FirstRunDone { get; set; }
    public string CollectionsRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Paddock");

    public List<FenceConfig> Fences { get; set; } = [];
    public List<RuleConfig> Rules { get; set; } = [];
    public List<string> DisabledPlugins { get; set; } = [];

    [JsonIgnore] public bool IsHidden { get; set; }
}
