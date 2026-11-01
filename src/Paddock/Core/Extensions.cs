using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Paddock.Sdk;

namespace Paddock.Core;

/// <summary>Registriert Erweiterungen (eingebaut + Plugins) und lädt Plugin-Assemblies isoliert.</summary>
public sealed class ExtensionRegistry
{
    public List<IRuleCondition> Conditions { get; } = [];
    public List<IFenceCommand> FenceCommands { get; } = [];
    public List<IItemCommand> ItemCommands { get; } = [];
    public List<LoadedPlugin> Plugins { get; } = [];

    public sealed record LoadedPlugin(IPaddockPlugin Plugin, string Source, bool Enabled, string? Error);

    public ExtensionRegistry()
    {
        Conditions.AddRange(BuiltInConditions.All);
    }

    public IRuleCondition? Condition(string id) => Conditions.FirstOrDefault(c => c.Id == id);

    public void LoadPlugins(Func<IPaddockPlugin, IPaddockHost> hostFactory, ICollection<string> disabled)
    {
        var dirs = new List<string> { AppPaths.Plugins };
        var bundled = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (Directory.Exists(bundled)) dirs.Add(bundled);

        foreach (var root in dirs)
        foreach (var dir in Directory.EnumerateDirectories(root))
        foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
        {
            if (Path.GetFileName(dll).Equals("Paddock.Sdk.dll", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var ctx = new PluginLoadContext(dll);
                var asm = ctx.LoadFromAssemblyPath(dll);
                foreach (var type in SafeTypes(asm).Where(t => typeof(IPaddockPlugin).IsAssignableFrom(t) && !t.IsAbstract && t.IsPublic))
                {
                    var plugin = (IPaddockPlugin)Activator.CreateInstance(type)!;
                    if (Plugins.Any(p => p.Plugin.Id == plugin.Id)) continue;
                    if (disabled.Contains(plugin.Id)) { Plugins.Add(new(plugin, dll, false, null)); continue; }
                    try
                    {
                        plugin.Initialize(hostFactory(plugin));
                        Plugins.Add(new(plugin, dll, true, null));
                        Log.Info($"Plugin geladen: {plugin.DisplayName} ({plugin.Id})");
                    }
                    catch (Exception ex)
                    {
                        Plugins.Add(new(plugin, dll, false, ex.Message));
                        Log.Error($"Plugin {plugin.Id} konnte nicht starten", ex);
                    }
                }
            }
            catch (Exception ex) { Log.Error($"Plugin-Datei {dll}", ex); }
        }
    }

    static IEnumerable<Type> SafeTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }

    /// <summary>Jedes Plugin bekommt einen eigenen Ladekontext; das SDK wird immer vom Host geteilt.</summary>
    sealed class PluginLoadContext(string path) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(path), isCollectible: false)
    {
        readonly AssemblyDependencyResolver _resolver = new(path);

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == "Paddock.Sdk") return null; // Host-Version verwenden
            var p = _resolver.ResolveAssemblyToPath(name);
            return p is null ? null : LoadFromAssemblyPath(p);
        }

        protected override IntPtr LoadUnmanagedDll(string name)
        {
            var p = _resolver.ResolveUnmanagedDllToPath(name);
            return p is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(p);
        }
    }
}

/// <summary>Eingebaute Regel-Bedingungen.</summary>
public static class BuiltInConditions
{
    public static readonly IRuleCondition[] All =
    [
        new Simple("extension", "Dateiendung ist", ".png, .jpg, .pdf", (i, p) =>
            !i.IsDirectory && Split(p).Any(e => string.Equals(Normalize(e), i.Extension, StringComparison.OrdinalIgnoreCase))),
        new Simple("kind", "Dateiart ist", "Programm, Ordner, Bild, Dokument, Video, Audio, Archiv, Code", (i, p) =>
            Split(p).Any(k => string.Equals(FileKinds.Of(i), k, StringComparison.OrdinalIgnoreCase))),
        new Simple("name", "Name passt auf Muster", "Rechnung*, *screenshot*", (i, p) =>
            Split(p).Any(g => Wildcard(g).IsMatch(i.Name))),
        new Simple("regex", "Name passt auf regulären Ausdruck", "^IMG_\\d+", (i, p) =>
        {
            try { return Regex.IsMatch(i.Name, p, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)); }
            catch { return false; }
        }),
        new Simple("olderThan", "Älter als (Tage)", "30", (i, p) =>
            int.TryParse(p.Trim(), out var days) && i.LastWriteTime < DateTime.Now.AddDays(-days)),
    ];

    static IEnumerable<string> Split(string p) => p.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    static string Normalize(string ext) => ext.StartsWith('.') ? ext : "." + ext;

    static Regex Wildcard(string glob) =>
        new("^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);

    sealed class Simple(string id, string name, string hint, Func<ItemInfo, string, bool> match) : IRuleCondition
    {
        public string Id => id;
        public string DisplayName => name;
        public string ParameterHint => hint;
        public bool Matches(ItemInfo item, string parameter) => match(item, parameter);
    }
}

/// <summary>Grobe Einteilung von Dateien – für Regeln und „Desktop aufräumen“.</summary>
public static class FileKinds
{
    public const string App = "Programm", Folder = "Ordner", Image = "Bild", Document = "Dokument",
        Video = "Video", Audio = "Audio", Archive = "Archiv", Code = "Code", Other = "Sonstiges";

    static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase);

    static FileKinds()
    {
        void Add(string kind, params string[] exts) { foreach (var e in exts) Map[e] = kind; }
        Add(App, ".lnk", ".url", ".exe", ".appref-ms", ".msi", ".bat", ".cmd", ".ps1", ".website");
        Add(Image, ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".heic", ".tif", ".tiff", ".svg", ".ico", ".psd", ".raw", ".avif");
        Add(Document, ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".txt", ".md", ".rtf", ".csv", ".epub");
        Add(Video, ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".m4v");
        Add(Audio, ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma");
        Add(Archive, ".zip", ".7z", ".rar", ".tar", ".gz", ".iso", ".cab");
        Add(Code, ".cs", ".js", ".ts", ".py", ".json", ".xml", ".html", ".css", ".sln", ".csproj", ".java", ".go", ".rs", ".cpp", ".h", ".yaml", ".yml");
    }

    public static string Of(ItemInfo item) => item.IsDirectory ? Folder : Map.GetValueOrDefault(item.Extension, Other);

    public static readonly string[] Ordered = [App, Folder, Document, Image, Video, Audio, Archive, Code, Other];

    public static string TitleFor(string kind) => kind switch
    {
        App => "Programme", Folder => "Ordner", Image => "Bilder", Document => "Dokumente",
        Video => "Videos", Audio => "Musik", Archive => "Archive", Code => "Code", _ => "Sonstiges",
    };

    public static string? TintFor(string kind) => kind switch
    {
        App => null, Folder => "#3A6EA5", Image => "#8A3FFC", Document => "#1F8A70",
        Video => "#C2410C", Audio => "#BE185D", Archive => "#6B7280", Code => "#0E7490", _ => null,
    };
}
