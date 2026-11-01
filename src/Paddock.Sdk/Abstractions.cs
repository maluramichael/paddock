namespace Paddock.Sdk;

/// <summary>
/// Einstiegspunkt eines Plugins. Paddock lädt alle DLLs aus
/// <c>%APPDATA%\Paddock\plugins\&lt;PluginName&gt;\</c> und instanziiert jede öffentliche
/// Klasse, die dieses Interface implementiert (parameterloser Konstruktor).
/// </summary>
public interface IPaddockPlugin
{
    /// <summary>Eindeutige, stabile Kennung, z. B. "de.malura.beispiele".</summary>
    string Id { get; }
    string DisplayName { get; }
    string Description => "";

    /// <summary>Wird einmal beim Start auf dem UI-Thread aufgerufen. Hier Erweiterungen registrieren.</summary>
    void Initialize(IPaddockHost host);
}

/// <summary>Was Paddock einem Plugin zur Verfügung stellt.</summary>
public interface IPaddockHost
{
    Version HostVersion { get; }

    /// <summary>Eigenes Datenverzeichnis des Plugins (existiert bereits).</summary>
    string DataDirectory { get; }

    /// <summary>Alle aktuell vorhandenen Bereiche.</summary>
    IReadOnlyList<IFenceInfo> Fences { get; }

    /// <summary>Neue Bedingung für Sortierregeln („Wenn eine Datei auf dem Desktop landet und …“).</summary>
    void RegisterRuleCondition(IRuleCondition condition);

    /// <summary>Neuer Eintrag im Kontextmenü eines Bereichs (Rechtsklick auf die Titelleiste).</summary>
    void RegisterFenceCommand(IFenceCommand command);

    /// <summary>Neuer Eintrag im Kontextmenü von Dateien innerhalb eines Bereichs.</summary>
    void RegisterItemCommand(IItemCommand command);

    /// <summary>Zeigt eine Benachrichtigung im Infobereich.</summary>
    void Notify(string title, string message);

    /// <summary>Schreibt in die Paddock-Protokolldatei.</summary>
    void Log(string message);

    /// <summary>Lädt den Inhalt eines Bereichs neu.</summary>
    void Refresh(Guid fenceId);
}

/// <summary>Schreibgeschützte Sicht auf einen Bereich.</summary>
public interface IFenceInfo
{
    Guid Id { get; }
    string Title { get; }
    /// <summary>Ordner, dessen Inhalt der Bereich zeigt.</summary>
    string FolderPath { get; }
    /// <summary>true = Ordner-Portal auf einen vorhandenen Ordner, false = eigene Sammlung.</summary>
    bool IsPortal { get; }
}

/// <summary>Eine Datei oder ein Ordner.</summary>
public sealed record ItemInfo(string Path, string Name, bool IsDirectory, long Size, DateTime LastWriteTime)
{
    public string Extension => System.IO.Path.GetExtension(Path);

    public static ItemInfo From(string path)
    {
        if (System.IO.Directory.Exists(path))
        {
            var d = new System.IO.DirectoryInfo(path);
            return new ItemInfo(path, d.Name, true, 0, d.LastWriteTime);
        }
        var f = new System.IO.FileInfo(path);
        return new ItemInfo(path, f.Name, false, f.Exists ? f.Length : 0, f.Exists ? f.LastWriteTime : DateTime.MinValue);
    }
}

/// <summary>Bedingung für Sortierregeln. Muss schnell und ohne UI arbeiten.</summary>
public interface IRuleCondition
{
    /// <summary>Stabile Kennung (wird in der Konfiguration gespeichert).</summary>
    string Id { get; }
    /// <summary>Anzeige im Regel-Editor, z. B. „Dateiendung ist“.</summary>
    string DisplayName { get; }
    /// <summary>Platzhalter für das Eingabefeld, z. B. ".png, .jpg".</summary>
    string ParameterHint => "";
    /// <summary>Feste Auswahlwerte (z. B. Dateiarten). Ist die Liste gesetzt, zeigt der Editor ein Auswahlfeld statt Freitext.</summary>
    IReadOnlyList<string>? Choices => null;
    bool Matches(ItemInfo item, string parameter);
}

/// <summary>Befehl im Bereichs-Menü.</summary>
public interface IFenceCommand
{
    string Id { get; }
    string DisplayName { get; }
    bool CanExecute(IFenceInfo fence) => true;
    void Execute(IFenceInfo fence, IReadOnlyList<ItemInfo> selection);
}

/// <summary>Befehl im Datei-Kontextmenü eines Bereichs.</summary>
public interface IItemCommand
{
    string Id { get; }
    string DisplayName { get; }
    bool CanExecute(IReadOnlyList<ItemInfo> items) => true;
    void Execute(IFenceInfo fence, IReadOnlyList<ItemInfo> items);
}
