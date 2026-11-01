# Themes

A theme is a JSON file in `%APPDATA%\Paddock\themes\` (or `data\themes\` in portable mode). The file name is the theme id. Paddock watches the folder: saving a file updates every area right away, without a restart.

The quickest start is Settings → *Darstellung* → *Aktuelles Theme als Vorlage kopieren*. That copies the active theme into the folder and opens it.

Every field is optional. Missing values fall back to the defaults shown below. Comments (`//`) and trailing commas are allowed.

```jsonc
{
  "name": "Mitternacht",                 // shown in settings and the tray menu
  "description": "Dark acrylic glass.",
  "dark": true,                          // dark or light UI for settings, dialogs and native menus

  "backdrop": "acrylic",                 // acrylic | blur | solid  (solid = no blur, cheapest)
  "corners": "round",                    // round | small | square  (Windows 11)
  "tint": "#141824",                     // area colour laid over the blur
  "tintOpacity": 0.42,                   // 0..1
  "border": "#2AFFFFFF",                 // #AARRGGBB, mixed into the window border; alpha 00 = no border
  "accent": "#7C8CFF",                   // drop highlight, toggles, selection
  "fontFamily": "Segoe UI Variable Text, Segoe UI",

  "title": {
    "fontFamily": "Segoe UI Variable Display, Segoe UI",
    "fontSize": 13,
    "fontWeight": "SemiBold",            // Normal, Medium, SemiBold, Bold …
    "foreground": "#F3F5FA",
    "background": "#10FFFFFF",
    "height": 34,                        // device-independent pixels
    "alignment": "left",                 // left | center | right
    "uppercase": false
  },

  "item": {
    "fontSize": 12,
    "foreground": "#EDEFF5",
    "hover": "#17FFFFFF",
    "selected": "#337C8CFF",
    "selectedBorder": "#997C8CFF",
    "spacing": 4
  },

  "menu": {
    "background": "#F51B1F29",
    "foreground": "#F3F5FA",
    "muted": "#9AA3B5",
    "hover": "#1FFFFFFF",
    "border": "#2EFFFFFF"
  },

  "resources": "my-theme.xaml"           // optional, see below
}
```

Colours use WPF notation: `#RRGGBB`, `#AARRGGBB` or named colours such as `White`.

## Built-in themes

| Id | Look |
|----|------|
| `mitternacht` | Dark acrylic, indigo accent (default) |
| `frost` | Light acrylic for bright wallpapers |
| `aurora` | Violet glass, turquoise accent, centred titles |
| `graphit` | Solid, no blur: the most resource-friendly option |
| `klar` | Almost invisible: blur only, no frame, no title background |

## Deeper changes with XAML

`resources` points to a XAML `ResourceDictionary`, with a path relative to the theme file. It is merged into the application resources after the JSON values, so it can override any of the `P.*` resource keys or add styles:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <SolidColorBrush x:Key="P.Title.Foreground" Color="Gold"/>
    <FontFamily x:Key="P.Title.FontFamily">Cascadia Code</FontFamily>
</ResourceDictionary>
```

Resource keys you can use: `P.Tint`, `P.Border`, `P.Accent`, `P.AccentSoft`, `P.FontFamily`, `P.CornerRadius`, `P.Title.*` (FontFamily, FontSize, FontWeight, Foreground, Background, Height, Alignment), `P.Item.*` (FontSize, Foreground, Hover, Selected, SelectedBorder, Spacing) and `P.Menu.*` (Background, Foreground, Muted, Hover, Border).
