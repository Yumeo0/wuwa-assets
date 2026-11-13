# Wuthering Waves assets

Extract selected game artwork and Spine animations with the .NET asset tool.
Run without arguments (or with `--help`) to show the available parameters:

```powershell
dotnet run --project tools/wuwa-extract
dotnet run --project tools/wuwa-extract -- --characters --weapons --quiet
dotnet run --project tools/wuwa-extract -- --all --quiet
# Override the detected game installation:
dotnet run --project tools/wuwa-extract -- --maps --paks 'D:\Wuthering Waves\Client' --quiet
```

Asset selectors can be combined:

| Parameter | Assets |
| --- | --- |
| `--characters` | Character cards, portraits and team artwork |
| `--echoes` | Echo artwork, monster icons and sonata set icons |
| `--weapons` | Weapon artwork and icons |
| `--skins` | Character, weapon and other cosmetic artwork |
| `--gacha` | Static gacha artwork |
| `--items` | Currency, food, gadgets and upgrade material icons |
| `--icons` | UI, attribute, element, emote, title and navigation icons |
| `--maps` | World-map tiles, map artwork and map icon atlases |
| `--spine` | Animated character artwork, skeletons, atlases and texture pages |
| `--textures` | All texture categories above |
| `--all` | All supported texture categories **and** Spine bundles |

`--all` runs both exporters in one invocation. UI atlases are also split into
individual sprite PNGs. `VERSION.json` records the texture extraction's game
version in the output root; `SPINE.json` records the Spine extraction separately.
The supported categories are the UI artwork and character animations listed here.

Without an asset selection, the tool prints help and does not open the game
archives or fetch keys. `--dir` alone selects custom texture directories;
`--list-dirs`, `--list-files` and `--inspect` also work without a selector.

Use `--list-dirs` to inspect the extraction targets and `--force` to overwrite
existing files when refreshing them after a game update. `--all` and `--textures`
also refresh existing PNG directories in the output root. Individual categories
stay limited to their preset directories. `--dir` replaces preset roots for the
selected formats and disables existing-directory discovery; repeat it for
multiple roots. `--exclude` skips a directory and its descendants, including
descendants of a broader selected root. `--dry-run` lists pending outputs without
writing files. App repositories consume these PNGs and handle their own WebP
conversion and publication.

## Animated character artwork (Spine)

The game's animated 2D character artwork uses **Spine**, rather than Live2D
Cubism. Export the original skeletons and atlases, together with their texture
pages:

```powershell
dotnet run --project tools/wuwa-extract -- --spine --quiet
# Inspect without writing, or select one character instead of the default roots:
dotnet run --project tools/wuwa-extract -- --spine --dry-run
dotnet run --project tools/wuwa-extract -- --spine --dir UI/UIResources/Common/Spine/Portraits/Portraits_Anke
# Export to another directory or refresh existing files after a game update:
dotnet run --project tools/wuwa-extract -- --spine --out D:/Extracted/Wuwa --force --quiet
```

The default Spine roots include:

- `UI/UIResources/Common/Spine`: formation portraits, older full-body art,
  costume variants, and chibis.
- `UI/UIResources/UiLuckdraw/Spine/Character`: animated gacha character artwork.

Paths mirror the archive layout relative to `Client/Content/Aki`. Each bundle
contains the original binary `.skel` (or `.json` skeleton), an unchanged `.atlas`,
and PNG texture pages **next to the atlas under the exact filenames it uses**.
Skeleton export names can differ from the atlas name; `SPINE.json` in the output
root records each pairing, its Spine version, texture pages, and source package.
It records the installed game version independently of the texture-only
`VERSION.json`. It is written only after a successful extraction.

Each model also includes:

- `characterId`: the owning game's role ID when there is exactly one owner.
- `characterIds`: every applicable role ID. Shared Rover portraits/skins list
  each element variant; use this array to find all models for a character.
- `category`: `portrait`, `gacha`, `skin`, `full-body`, `chibi`, or `other`.
- `characterReferences`: the character IDs and config/prefab references that
  established ownership.

Ownership is resolved automatically from the mounted game's SQLite config
databases and generated FlatBuffer readers. Portraits/skins use `roleskin` and
trial portrait references. Gacha ownership follows the banner's role ID through
`SpinePrefabResource`, `uiresource`, and the prefab's exact skeleton references.
No separate data checkout, hard-coded character names, or fixed schema field
offsets are required. The repeated Encore placeholder in `roleinfo` is not used.

An unreferenced model has `characterId: null` and `characterIds: []`; a shared
model has `characterId: null` and all valid IDs in `characterIds`. Non-character
art (such as the Abu portrait), older full-body variants, and chibis may lack an
owner. Unmapped portrait/gacha entries produce a warning. Re-running `--spine`
rebuilds this metadata even when all asset files already exist; `--force` is
unnecessary for updating the index.

Use a Spine viewer/runtime matching the major/minor version in `SPINE.json`
(the installed 3.8.0 character assets use Spine 4.1). These are rigged artwork
animations, separate from the game's 3D combat models. Animation tracks are
embedded in the skeleton; no separate motion file is needed. The extractor
preserves the payload without converting it to a different Spine version or
Live2D format.

For additional animation categories, `--dir` replaces the default Spine roots
(repeat it to select multiple roots); `--exclude` excludes descendants as well:

```powershell
dotnet run --project tools/wuwa-extract -- --spine --dir UI/UIResources/UiActivity/Spine
dotnet run --project tools/wuwa-extract -- --list-files Spine
dotnet run --project tools/wuwa-extract -- --inspect Client/Content/Aki/UI/UIResources/Common/Spine/Portraits/Portraits_Anke/Portraits_Anke
```

`--paks`, AES key options, and `--mappings` work in both extraction modes.
Existing files are skipped unless `--force` is set. Texture selectors do not
refresh exported Spine bundles; include `--spine` or use `--all` to keep their
skeletons, atlases and texture pages together. A targeted Spine run writes an
index of its selected bundles; use a separate `--out` directory for an isolated
export.

## Checks

The option, target-selection and character-mapping checks do not require a game installation:

```powershell
dotnet run --project tools/wuwa-extract.Tests
```
