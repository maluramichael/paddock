# Plugins

Plugins are ordinary .NET 9 class libraries that reference `Paddock.Sdk`. Paddock loads every DLL from

- `%APPDATA%\Paddock\plugins\<PluginName>\` (your own plugins), and
- `<install dir>\plugins\<PluginName>\` (plugins shipped with the installer),

and creates every public class that implements `IPaddockPlugin`. Each plugin gets its own `AssemblyLoadContext`, so its dependencies do not clash with other plugins. `Paddock.Sdk` itself is always shared with the host.

## Extension points

| Interface | Shows up as |
|-----------|-------------|
| `IRuleCondition` | A new condition in Settings → *Regeln*, used for sorting new desktop files |
| `IFenceCommand` | An entry in an area's menu (right-click the title) |
| `IItemCommand` | An entry at the top of a file's context menu inside an area |

`IPaddockHost`, which your plugin receives in `Initialize`, also gives you `Fences`, a private `DataDirectory`, `Notify` (tray balloon), `Log` and `Refresh(fenceId)`.

## Minimal plugin

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <!-- Reference the SDK, but do not copy it: the host provides it -->
    <ProjectReference Include="..\..\src\Paddock.Sdk\Paddock.Sdk.csproj" Private="false" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
```

```csharp
using Paddock.Sdk;

public sealed class HelloPlugin : IPaddockPlugin
{
    public string Id => "com.example.hello";
    public string DisplayName => "Hello";

    public void Initialize(IPaddockHost host)
    {
        host.RegisterRuleCondition(new ScreenshotCondition());
        host.RegisterFenceCommand(new CountCommand(host));
    }
}

sealed class ScreenshotCondition : IRuleCondition
{
    public string Id => "hello.screenshot";
    public string DisplayName => "Is a screenshot";
    public bool Matches(ItemInfo item, string parameter) =>
        item.Name.StartsWith("Screenshot", StringComparison.OrdinalIgnoreCase);
}

sealed class CountCommand(IPaddockHost host) : IFenceCommand
{
    public string Id => "hello.count";
    public string DisplayName => "Count files";
    public void Execute(IFenceInfo fence, IReadOnlyList<ItemInfo> selection) =>
        host.Notify(fence.Title, $"{Directory.EnumerateFileSystemEntries(fence.FolderPath).Count()} items");
}
```

Build it and copy the output DLL (without `Paddock.Sdk.dll`) to `%APPDATA%\Paddock\plugins\Hello\`, then restart Paddock. Settings → *Erweiterungen* lists every plugin, shows load errors and lets you switch plugins off.

A complete example covering all three extension points lives in [`plugins/Paddock.Plugins.Examples`](../plugins/Paddock.Plugins.Examples).

## Guidelines

- `Initialize` and every command run on the UI thread. Keep them quick and move long work to `Task.Run`.
- Give `IRuleCondition.Choices` a list of values if your condition only accepts a fixed set. The rule editor then shows a dropdown instead of a text field.
- `IRuleCondition.Matches` runs for every new desktop file. It must be fast and must not show any UI.
- Exceptions from commands are caught, logged and shown as a tray notification. They do not crash Paddock.
- Keep the plugin `Id` stable, because rules and the list of disabled plugins refer to it.
