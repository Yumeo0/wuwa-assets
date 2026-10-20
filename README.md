# Wuthering Waves assets

Extract game textures into this checkout with the .NET asset tool:

```powershell
dotnet run --project tools/wuwa-extract -- --quiet
# Override the detected game installation:
dotnet run --project tools/wuwa-extract -- --paks 'D:\Wuthering Waves\Client' --quiet
```

The default targets include character, echo and weapon artwork, map tiles
(`UI/UIResources/UiWorldMap/Image`), world-map icon atlases
(`UI/UIResources/Common/Atlas/WorldMapIcon`) and tab/nation icon atlases
(`UI/UIResources/Common/Atlas/TabIcon`). Atlases are also split into individual
sprite PNGs. `VERSION.json` records the installed game version.

Use `--list-dirs` to inspect the extraction targets and `--force` to overwrite
existing images when refreshing them after a game update. App repositories
consume these PNGs and handle their own WebP conversion and publication.
