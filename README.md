# WSCrafter

GameHelper / VaalHub plugin for Path of Exile 2 waystone crafting automation.

Fork of [Ionic28/WSCrafter](https://github.com/Ionic28/WSCrafter), adapted for this workspace and localized for VaalHub.

## Features

- Select inventory slots and plan waystone crafting
- Alchemy maps below rare
- Exalt rare waystones to a target explicit mod count
- Optional final Vaal step
- Overlay highlights, emergency stop key, and debug tools
- Click automation uses `Core.Process.WindowArea` to convert game-client UI positions to screen coordinates (aligned with ItemMove)
- 11-language UI localization

## Display names

| Language | Name |
|---|---|
| English | Waystone Crafter |
| 繁體中文 | 換界石制作 |
| 简体中文 | 引路石制作 |

Waystone terms follow [poe2db](https://poe2db.tw/tw/Waystones) language pages where available.

## Build

From the workspace root:

```powershell
dotnet build .\plugins\WSCrafter\WSCrafter.csproj -c Release -p:GameHelperCoreDir="$PWD\VaalHub"
```

## Credits

- Original plugin: **Ionic28**
- VaalHub fork / localization: workspace maintainers
