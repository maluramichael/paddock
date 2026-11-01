using System.IO.Compression;
using Paddock.Sdk;

namespace Paddock.Plugins.Examples;

/// <summary>
/// Beispiel-Plugin: zeigt alle drei Erweiterungspunkte.
/// Bauen und den Ausgabeordner nach %APPDATA%\Paddock\plugins\Examples\ kopieren.
/// </summary>
public sealed class ExamplesPlugin : IPaddockPlugin
{
    public string Id => "de.malura.paddock.beispiele";
    public string DisplayName => "Beispiel-Erweiterungen";
    public string Description => "Regel „Größer als (MB)“, ZIP-Archiv aus Auswahl, Bereich sichern.";

    public void Initialize(IPaddockHost host)
    {
        host.RegisterRuleCondition(new LargerThanCondition());
        host.RegisterItemCommand(new ZipItemsCommand(host));
        host.RegisterFenceCommand(new BackupFenceCommand(host));
        host.Log("Beispiel-Erweiterungen bereit.");
    }
}

/// <summary>Regel-Bedingung: Datei ist größer als N Megabyte.</summary>
sealed class LargerThanCondition : IRuleCondition
{
    public string Id => "examples.largerThan";
    public string DisplayName => "Größer als (MB)";
    public string ParameterHint => "100";

    public bool Matches(ItemInfo item, string parameter) =>
        !item.IsDirectory
        && double.TryParse(parameter.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mb)
        && item.Size > mb * 1024 * 1024;
}

/// <summary>Datei-Befehl: packt die Auswahl als ZIP neben die Dateien.</summary>
sealed class ZipItemsCommand(IPaddockHost host) : IItemCommand
{
    public string Id => "examples.zip";
    public string DisplayName => "Als ZIP-Archiv packen";

    public void Execute(IFenceInfo fence, IReadOnlyList<ItemInfo> items)
    {
        var dir = Path.GetDirectoryName(items[0].Path)!;
        var baseName = items.Count == 1 ? Path.GetFileNameWithoutExtension(items[0].Name) : "Archiv";
        var target = Unique(Path.Combine(dir, baseName + ".zip"));
        using (var zip = ZipFile.Open(target, ZipArchiveMode.Create))
        {
            foreach (var item in items)
            {
                if (item.IsDirectory)
                    foreach (var file in Directory.EnumerateFiles(item.Path, "*", SearchOption.AllDirectories))
                        zip.CreateEntryFromFile(file, Path.Combine(item.Name, Path.GetRelativePath(item.Path, file)));
                else zip.CreateEntryFromFile(item.Path, item.Name);
            }
        }
        host.Refresh(fence.Id);
        host.Notify("ZIP erstellt", Path.GetFileName(target));
    }

    static string Unique(string path)
    {
        var dir = Path.GetDirectoryName(path)!; var name = Path.GetFileNameWithoutExtension(path);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{name} ({i}).zip");
        return path;
    }
}

/// <summary>Bereichs-Befehl: sichert den ganzen Bereich als ZIP in „Dokumente\Paddock-Sicherungen“.</summary>
sealed class BackupFenceCommand(IPaddockHost host) : IFenceCommand
{
    public string Id => "examples.backup";
    public string DisplayName => "Bereich als ZIP sichern";

    public bool CanExecute(IFenceInfo fence) => Directory.Exists(fence.FolderPath);

    public void Execute(IFenceInfo fence, IReadOnlyList<ItemInfo> selection)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Paddock-Sicherungen");
        Directory.CreateDirectory(dir);
        var safe = string.Concat(fence.Title.Split(Path.GetInvalidFileNameChars()));
        var target = Path.Combine(dir, $"{safe} {DateTime.Now:yyyy-MM-dd HH-mm}.zip");
        if (File.Exists(target)) File.Delete(target);
        ZipFile.CreateFromDirectory(fence.FolderPath, target, CompressionLevel.Optimal, includeBaseDirectory: false);
        host.Notify("Bereich gesichert", target);
    }
}
