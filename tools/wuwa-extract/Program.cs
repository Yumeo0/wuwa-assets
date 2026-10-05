using System.Diagnostics;
using System.Text.Json;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using Newtonsoft.Json;
using SkiaSharp;

namespace WuwaExtract;

internal static class Program
{
    private const StringComparison Ico = StringComparison.OrdinalIgnoreCase;
    private const string AkiMarker = "Aki/";
    private const string UassetExtension = ".uasset";
    private const string AtlasPrefix = "T_TPI_";
    private const string AtlasSuffix = "_UIAtlas";
    private const string ImporterPrefix = "TPI_";
    private const string ImporterExportType = "LGUITexturePackerImporterData";
    private const string SpriteDataExportType = "LGUITexturePackerSpriteData";
    private const string TextureDataExportType = "LGUITextureData";
    private const string DefaultKeysEndpoint = "https://yarik0chka.github.io/wuwa-keys/keys.json";
    private const string FallbackMainKey = "0x6F80948821CA338739A24D4D9F778BCAC0996B2EF2A73897A789C68AFF05174E";

    private static readonly string[] DefaultDirectories =
    [
        "UI/UIResources/Common/Image/Card",
        "UI/UIResources/Common/Image/CardLong",
        "UI/UIResources/Common/Image/CardPolygon",
        "UI/UIResources/Common/Image/IconAttribute",
        "UI/UIResources/Common/Image/IconC",
        "UI/UIResources/Common/Image/IconChatEmoticon",
        "UI/UIResources/Common/Image/IconCook",
        "UI/UIResources/Common/Image/IconDevice",
        "UI/UIResources/Common/Image/IconElement128",
        "UI/UIResources/Common/Image/IconElementAttri128",
        "UI/UIResources/Common/Image/IconElementRound",
        "UI/UIResources/Common/Image/IconElementSuit",
        "UI/UIResources/Common/Image/IconForceLogo",
        "UI/UIResources/Common/Image/IconLogo",
        "UI/UIResources/Common/Image/IconMonsterGoods160",
        "UI/UIResources/Common/Image/IconMout",
        "UI/UIResources/Common/Image/IconRoleHead256",
        "UI/UIResources/Common/Image/IconRoleHeadCircle256",
        "UI/UIResources/Common/Image/IconRolePile",
        "UI/UIResources/Common/Image/IconRolePlayerHead256",
        "UI/UIResources/Common/Image/IconRup",
        "UI/UIResources/Common/Image/IconWeapon",
        "UI/UIResources/Common/Image/IconWeaponSkin",
        "UI/UIResources/Common/Image/IconWup",
        "UI/UIResources/Common/Image/ImgNum",
        "UI/UIResources/Common/Image/ImgQuality",
        "UI/UIResources/Common/Image/Luckdraw",
        "UI/UIResources/Common/Image/PixActivity",
        "UI/UIResources/Common/Image/Skin",
        "UI/UIResources/Common/Image/TitleDesc",
        "UI/UIResources/UiTeam/Image"
    ];

    private static readonly string[] AutoDetectRoots =
    [
        @"C:\Program Files\Wuthering Waves",
        @"C:\Program Files (x86)\Wuthering Waves",
        @"C:\Wuthering Waves",
        @"D:\Wuthering Waves",
        @"D:\Program Files\Wuthering Waves",
        @"D:\Program Files (x86)\Wuthering Waves",
        @"C:\Program Files (x86)\Steam\steamapps\common\Wuthering Waves",
        @"C:\Program Files\Steam\steamapps\common\Wuthering Waves",
        @"D:\SteamLibrary\steamapps\common\Wuthering Waves",
        @"D:\Steam\steamapps\common\Wuthering Waves"
    ];

    private static int Main(string[] args)
    {
        var options = new Options();
        if (!options.Parse(args))
        {
            Console.Error.WriteLine(options.Error);
            Console.Error.WriteLine();
            PrintUsage();
            return 1;
        }

        if (options.Help)
        {
            PrintUsage();
            return 0;
        }

        var outDir = ResolveOutputDirectory(options);
        var targets = CollectTargets(outDir, options);

        if (options.ListDirs)
        {
            Console.WriteLine($"Output root: {outDir}");
            foreach (var target in targets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine("  " + target);
            }
            Console.WriteLine($"{targets.Count} target directories");
            return 0;
        }

        var paksDir = ResolvePaksDirectory(options.Paks);
        if (paksDir is null)
        {
            Console.Error.WriteLine("error: could not locate the game Paks directory.");
            Console.Error.WriteLine("Pass --paks <dir> (the Paks folder or the game/client root) or set WUWA_PAKS.");
            return 1;
        }

        Console.WriteLine($"Paks:   {paksDir}");
        Console.WriteLine($"Output: {outDir}");
        Console.WriteLine($"Targets: {targets.Count} directories");

        var (mainKey, dynamicKeys) = LoadKeys(options);
        if (mainKey is null)
        {
            Console.Error.WriteLine("error: no AES key available. Pass --key, --keys-file or allow the keys endpoint.");
            return 1;
        }

        var searchOption = options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var extraDirectories = FindExtraArchiveDirectories(paksDir);
        if (extraDirectories.Length > 0)
        {
            Console.WriteLine("Extra:  " + string.Join(", ", extraDirectories.Select(x => x.FullName)));
        }

        TextureDecoder.UseAssetRipperTextureDecoder = true;

        using var provider = new DefaultFileProvider(new DirectoryInfo(paksDir), extraDirectories, searchOption, new VersionContainer(EGame.GAME_WutheringWaves), StringComparer.OrdinalIgnoreCase);
        provider.Initialize();

        if (options.Mappings is not null)
        {
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(options.Mappings);
            Console.WriteLine($"Mappings: {options.Mappings}");
        }

        provider.Mount();

        var keyMap = new Dictionary<FGuid, FAesKey>();
        if (mainKey is not null) keyMap[new FGuid()] = new FAesKey(mainKey);
        var invalidKeys = 0;
        foreach (var (guid, key) in dynamicKeys)
        {
            try
            {
                keyMap[new FGuid(guid)] = new FAesKey(key);
            }
            catch (ArgumentException)
            {
                invalidKeys++;
            }
        }

        var mounted = provider.SubmitKeys(keyMap);
        provider.PostMount();

        Console.WriteLine($"Mounted {mounted} pak file(s), {provider.Files.Count} files indexed, {provider.RequiredKeys.Count} key(s) missing");
        if (invalidKeys > 0)
        {
            Console.Error.WriteLine($"warning: ignored {invalidKeys} malformed dynamic key(s)");
        }

        if (provider.RequiredKeys.Count > 0)
        {
            Console.Error.WriteLine("warning: some archives could not be opened with the available keys:");
            foreach (var guid in provider.RequiredKeys.Take(10))
            {
                Console.Error.WriteLine("  required key: " + guid);
            }
            Console.Error.WriteLine("Try a fresh keys file from the endpoint (the game may have shipped a new key).");
        }

        if (provider.Files.Count == 0)
        {
            Console.Error.WriteLine("error: no files were mounted. Check the Paks directory, AES keys and whether the game version needs new keys.");
            return 1;
        }

        var targetSet = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            targetSet[target] = target;
        }

        var matches = new List<(string ObjectPath, string Relative, string Canonical, string Tail)>();
        var importersByDirectory = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, _) in provider.Files)
        {
            if (!path.EndsWith(UassetExtension, Ico)) continue;

            var markerIndex = path.LastIndexOf(AkiMarker, Ico);
            if (markerIndex < 0) continue;

            var relative = path[(markerIndex + AkiMarker.Length)..];
            if (relative.Length == 0) continue;

            var objectPath = path[..^UassetExtension.Length];
            if (Path.GetFileName(relative).StartsWith(ImporterPrefix, Ico))
            {
                var directory = objectPath[..objectPath.LastIndexOf('/')];
                if (!importersByDirectory.TryGetValue(directory, out var importers))
                {
                    importersByDirectory[directory] = importers = [];
                }
                importers.Add(objectPath);
            }

            var dirEnd = relative.LastIndexOf('/');
            while (dirEnd > 0)
            {
                var candidate = relative[..dirEnd];
                if (targetSet.TryGetValue(candidate, out var canonical))
                {
                    matches.Add((objectPath, relative, canonical, relative[dirEnd..]));
                    break;
                }

                dirEnd = relative.LastIndexOf('/', dirEnd - 1);
            }
        }

        if (matches.Count == 0)
        {
            Console.Error.WriteLine("error: no UI textures matched the target directories inside the archives.");
            Console.Error.WriteLine("Archive paths are expected to contain \"Client/Content/Aki/...\".");
            return 1;
        }

        var importerCache = new Dictionary<string, AtlasImporter?>(StringComparer.OrdinalIgnoreCase);
        var items = new Dictionary<string, ExtractionItem>(StringComparer.OrdinalIgnoreCase);
        var spriteItems = new List<ExtractionItem>();
        foreach (var (objectPath, relative, canonical, tail) in matches)
        {
            var outputRel = canonical + tail[..^UassetExtension.Length] + ".png";
            var outputPath = Path.Combine(outDir, outputRel.Replace('/', Path.DirectorySeparatorChar));
            items.TryAdd(outputPath, new ExtractionItem(objectPath, relative, outputPath, null));

            if (options.NoSplitAtlas) continue;
            if (!IsAtlasAsset(Path.GetFileName(relative))) continue;

            spriteItems.AddRange(ExpandAtlas(provider, objectPath, outputPath, importersByDirectory, importerCache));
        }

        foreach (var spriteItem in spriteItems)
        {
            items[spriteItem.OutputPath] = spriteItem;
        }

        var pending = new List<ExtractionItem>();
        var skipped = 0;
        foreach (var item in items.Values.OrderBy(x => x.OutputPath, StringComparer.OrdinalIgnoreCase))
        {
            if (!options.Force && File.Exists(item.OutputPath))
            {
                skipped++;
                continue;
            }
            pending.Add(item);
        }

        Console.WriteLine($"{items.Count} matched asset(s): {pending.Count} to extract, {skipped} already present");

        if (options.DryRun)
        {
            foreach (var item in pending)
            {
                Console.WriteLine("  " + Path.GetRelativePath(outDir, item.OutputPath).Replace('\\', '/'));
            }
            Console.WriteLine($"dry run: would write {pending.Count} PNG file(s)");
            return 0;
        }

        if (pending.Count == 0)
        {
            Console.WriteLine("Nothing to do.");
            return 0;
        }

        var stopwatch = Stopwatch.StartNew();
        var extracted = 0;
        var sprites = 0;
        var ignored = 0;
        var failed = 0;
        var failureLines = new List<string>();
        var atlasBitmaps = new Dictionary<string, SKBitmap>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (var i = 0; i < pending.Count; i++)
            {
                var item = pending[i];
                try
                {
                    if (item.Sprite is { } sprite)
                    {
                        var atlas = GetAtlasBitmap(provider, atlasBitmaps, item.ObjectPath);
                        using var cropped = CropSprite(atlas, sprite);
                        using var image = SKImage.FromBitmap(cropped);
                        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                        WriteFile(item.OutputPath, data.ToArray());
                        extracted++;
                        sprites++;
                    }
                    else
                    {
                        var obj = provider.LoadPackageObject(item.ObjectPath);
                        if (obj is UTexture texture)
                        {
                            WriteFile(item.OutputPath, EncodeTexture(texture, atlasBitmaps, item.ObjectPath));
                            extracted++;
                        }
                        else if (obj.ExportType is ImporterExportType or SpriteDataExportType or TextureDataExportType)
                        {
                            ignored++;
                            continue;
                        }
                        else
                        {
                            throw new InvalidDataException($"export is {obj.ExportType}, not a UTexture");
                        }
                    }

                    if (!options.Quiet)
                    {
                        Console.WriteLine($"[{i + 1}/{pending.Count}] {item.OutputPath}");
                    }
                }
                catch (Exception e)
                {
                    failed++;
                    if (failureLines.Count < 25)
                    {
                        failureLines.Add($"{item.RelativePath}: {e.Message}");
                    }
                }
            }
        }
        finally
        {
            foreach (var bitmap in atlasBitmaps.Values)
            {
                bitmap.Dispose();
            }
        }

        stopwatch.Stop();
        foreach (var line in failureLines)
        {
            Console.Error.WriteLine("FAIL " + line);
        }
        if (failed > failureLines.Count)
        {
            Console.Error.WriteLine($"FAIL ... and {failed - failureLines.Count} more");
        }

        Console.WriteLine($"Done in {stopwatch.Elapsed:mm\\:ss\\.ff}: {extracted} extracted ({sprites} sprites), {ignored} ignored, {skipped} skipped, {failed} failed");
        return 0;
    }

    private static bool IsAtlasAsset(string fileName) =>
        fileName.StartsWith(AtlasPrefix, Ico) && fileName.Contains(AtlasSuffix, Ico);

    private static IEnumerable<ExtractionItem> ExpandAtlas(
        IFileProvider provider,
        string atlasObjectPath,
        string atlasOutputPath,
        IReadOnlyDictionary<string, List<string>> importersByDirectory,
        Dictionary<string, AtlasImporter?> importerCache)
    {
        var normalizedAtlas = NormalizeAssetPath(atlasObjectPath);
        var directory = atlasObjectPath[..atlasObjectPath.LastIndexOf('/')];

        var importer = LoadImporter(provider, DeriveImporterPath(provider, atlasObjectPath), importerCache)
                       ?? FindImporterForAtlas(provider, directory, importersByDirectory, normalizedAtlas, importerCache);
        if (importer is null) yield break;

        var page = importer.AtlasPaths.FindIndex(p => p.Equals(normalizedAtlas, Ico));
        if (page < 0) yield break;

        var outputDirectory = atlasOutputPath[..atlasOutputPath.LastIndexOf(Path.DirectorySeparatorChar)];
        for (var index = 0; index < importer.Sprites.Count; index++)
        {
            if (!importer.PageBySprite.TryGetValue(index, out var spritePage) || spritePage != page) continue;
            if (!importer.RectBySprite.TryGetValue(index, out var rect)) continue;

            var name = Path.GetFileNameWithoutExtension(importer.Sprites[index]);
            if (name.Length == 0) continue;

            var outputPath = Path.Combine(outputDirectory, name + ".png");
            yield return new ExtractionItem(atlasObjectPath, name, outputPath, rect);
        }
    }

    private static string? DeriveImporterPath(IFileProvider provider, string atlasObjectPath)
    {
        var slash = atlasObjectPath.LastIndexOf('/');
        if (slash < 0) return null;

        var name = atlasObjectPath[(slash + 1)..];
        if (!name.StartsWith(AtlasPrefix, Ico)) return null;

        var core = name[AtlasPrefix.Length..];
        var suffixIndex = core.LastIndexOf(AtlasSuffix, Ico);
        if (suffixIndex < 0) return null;

        core = core[..suffixIndex].TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (core.Length == 0) return null;

        var derived = atlasObjectPath[..slash] + "/" + ImporterPrefix + core;
        return provider.TryGetGameFile(derived, out _) ? derived : null;
    }

    private static AtlasImporter? FindImporterForAtlas(
        IFileProvider provider,
        string directory,
        IReadOnlyDictionary<string, List<string>> importersByDirectory,
        string normalizedAtlas,
        Dictionary<string, AtlasImporter?> importerCache)
    {
        if (!importersByDirectory.TryGetValue(directory, out var candidates)) return null;

        foreach (var candidate in candidates)
        {
            var importer = LoadImporter(provider, candidate, importerCache);
            if (importer is not null && importer.AtlasPaths.Any(p => p.Equals(normalizedAtlas, Ico)))
            {
                return importer;
            }
        }

        return null;
    }

    private static AtlasImporter? LoadImporter(IFileProvider provider, string? importerPath, Dictionary<string, AtlasImporter?> cache)
    {
        if (importerPath is null) return null;
        if (cache.TryGetValue(importerPath, out var cached)) return cached;

        AtlasImporter? importer = null;
        try
        {
            var obj = provider.LoadPackageObject(importerPath);
            if (obj.ExportType.Equals(ImporterExportType, Ico))
            {
                importer = AtlasImporter.Parse(obj);
                if (importer is null)
                {
                    Console.Error.WriteLine($"warning: could not read sprite layout from {importerPath}, keeping the atlas only");
                }
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"warning: failed to load atlas importer {importerPath}: {e.Message}");
        }

        cache[importerPath] = importer;
        return importer;
    }

    private static string NormalizeAssetPath(string path)
    {
        var dot = path.LastIndexOf('.');
        if (dot > path.LastIndexOf('/'))
        {
            path = path[..dot];
        }

        var markerIndex = path.LastIndexOf(AkiMarker, Ico);
        return markerIndex >= 0 ? path[(markerIndex + AkiMarker.Length)..] : path.Trim('/');
    }

    private static SKBitmap GetAtlasBitmap(IFileProvider provider, Dictionary<string, SKBitmap> cache, string atlasObjectPath)
    {
        if (cache.TryGetValue(atlasObjectPath, out var cached)) return cached;

        var obj = provider.LoadPackageObject(atlasObjectPath);
        if (obj is not UTexture texture)
        {
            throw new InvalidDataException($"atlas {atlasObjectPath} is not a UTexture");
        }

        var decoded = texture.Decode(ETexturePlatform.DesktopMobile) ?? throw new InvalidDataException("atlas has no decodable mip");
        var bitmap = decoded.ToSkBitmap();
        cache[atlasObjectPath] = bitmap;
        return bitmap;
    }

    private static SKBitmap CropSprite(SKBitmap source, AtlasSprite sprite)
    {
        var x = (int) MathF.Round(sprite.U0X * source.Width);
        var y = (int) MathF.Round(sprite.U3Y * source.Height);
        var width = sprite.Width > 0 ? sprite.Width : (int) MathF.Round((sprite.U3X - sprite.U0X) * source.Width);
        var height = sprite.Height > 0 ? sprite.Height : (int) MathF.Round((sprite.U0Y - sprite.U3Y) * source.Height);

        x = Math.Clamp(x, 0, source.Width - 1);
        y = Math.Clamp(y, 0, source.Height - 1);
        width = Math.Clamp(width, 1, source.Width - x);
        height = Math.Clamp(height, 1, source.Height - y);

        var destination = new SKBitmap(new SKImageInfo(width, height, source.ColorType, source.AlphaType));
        using var canvas = new SKCanvas(destination);
        canvas.DrawBitmap(source, new SKRect(x, y, x + width, y + height), new SKRect(0, 0, width, height));
        return destination;
    }

    private static byte[] EncodeTexture(UTexture texture, Dictionary<string, SKBitmap> atlasBitmaps, string objectPath)
    {
        if (atlasBitmaps.TryGetValue(objectPath, out var cached))
        {
            using var image = SKImage.FromBitmap(cached);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        var decoded = texture.Decode(ETexturePlatform.DesktopMobile) ?? throw new InvalidDataException("texture has no decodable mip");
        return decoded.Encode(ETextureFormat.Png, false, out _);
    }

    private static void WriteFile(string path, byte[] data)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllBytes(path, data);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            wuwa-extract - extract Wuthering Waves UI textures into the wuwa-assets repository.

            Usage:
              dotnet run --project tools/wuwa-extract -- [options]

            Options:
              --paks <dir>           Paks directory or game/client root. Defaults to WUWA_PAKS or a common install path.
              --out <dir>            Output root. Defaults to the wuwa-assets repository root.
              --dir <relative>       Extra directory to extract, relative to Client/Content/Aki/ (repeatable).
              --exclude <relative>   Directory to skip (repeatable).
              --key <0x...>          Main AES key. Defaults to the key endpoint below.
              --keys-endpoint <url>  Key endpoint (default: https://yarik0chka.github.io/wuwa-keys/keys.json).
              --keys-file <file>     Read keys.json from a local file instead of the endpoint.
              --no-keys-endpoint     Do not fetch keys; use --key or the built-in fallback key.
              --mappings <file>      Optional .usmap mappings file.
              --recursive            Scan Paks subdirectories too.
              --no-split-atlas       Do not split UI atlases into individual sprite PNGs.
              --force                Overwrite PNG files that already exist.
              --dry-run              List what would be extracted without writing files.
              --list-dirs            Print the target directories and exit.
              --quiet                Only print warnings and the summary.
              -h, --help             Show this help.

            Targets:
              The built-in list covers the requested UI directories. In addition, every directory
              that already contains PNGs in the output root is refreshed as well.

            Atlases:
              Atlases (T_TPI_*_UIAtlas) are also split into individual sprite PNGs next to the
              atlas image, using the sprite layout from the matching TPI_* importer asset.
            """);
    }

    private static string ResolveOutputDirectory(Options options)
    {
        if (!string.IsNullOrWhiteSpace(options.Out))
        {
            return Path.GetFullPath(options.Out);
        }

        return WalkForAssetsRoot(AppContext.BaseDirectory)
               ?? WalkForAssetsRoot(Environment.CurrentDirectory)
               ?? Environment.CurrentDirectory;
    }

    private static string? WalkForAssetsRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "UI", "UIResources")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static List<string> CollectTargets(string outDir, Options options)
    {
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in DefaultDirectories.Concat(options.Directories))
        {
            var normalized = NormalizeTarget(directory);
            if (normalized.Length > 0)
            {
                targets.TryAdd(normalized, normalized);
            }
        }

        foreach (var directory in DiscoverExistingDirectories(outDir))
        {
            targets.TryAdd(directory, directory);
        }

        foreach (var exclude in options.Exclude)
        {
            var normalized = NormalizeTarget(exclude);
            if (normalized.Length == 0) continue;
            foreach (var key in targets.Keys
                         .Where(k => k.Equals(normalized, Ico) || k.StartsWith(normalized + "/", Ico))
                         .ToList())
            {
                targets.Remove(key);
            }
        }

        return targets.Values.ToList();
    }

    private static IEnumerable<string> DiscoverExistingDirectories(string outDir)
    {
        if (!Directory.Exists(outDir)) yield break;

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(outDir, "*", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var dir in dirs)
        {
            bool hasPng;
            try
            {
                hasPng = Directory.EnumerateFiles(dir, "*.png", SearchOption.TopDirectoryOnly).Any();
            }
            catch (Exception)
            {
                continue;
            }

            if (!hasPng) continue;

            var relative = Path.GetRelativePath(outDir, dir).Replace('\\', '/');
            var normalized = NormalizeTarget(relative);
            if (normalized.Length > 0)
            {
                yield return normalized;
            }
        }
    }

    private static readonly string[] TargetPrefixes =
    [
        "Client/Content/Aki/",
        "Aki/",
        "../../../Client/Content/Aki/"
    ];

    private static string NormalizeTarget(string directory)
    {
        var normalized = directory.Replace('\\', '/').Trim().Trim('/');
        foreach (var prefix in TargetPrefixes)
        {
            if (normalized.StartsWith(prefix, Ico))
            {
                normalized = normalized[prefix.Length..];
                break;
            }
        }
        return normalized;
    }

    private static string? ResolvePaksDirectory(string? candidate)
    {
        var fromEnv = candidate;
        if (string.IsNullOrWhiteSpace(fromEnv))
        {
            fromEnv = Environment.GetEnvironmentVariable("WUWA_PAKS");
        }

        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return ProbePaksDirectory(fromEnv);
        }

        foreach (var root in AutoDetectRoots)
        {
            var resolved = ProbePaksDirectory(root);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    private static string? ProbePaksDirectory(string candidate)
    {
        try
        {
            if (File.Exists(candidate))
            {
                candidate = Path.GetDirectoryName(candidate)!;
            }
            if (!Directory.Exists(candidate))
            {
                return null;
            }

            if (HasArchives(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            string[] relativeCandidates =
            [
                "Client/Saved/Paks",
                "Client/Content/Paks",
                "Wuthering Waves Game/Client/Saved/Paks",
                "Wuthering Waves Game/Client/Content/Paks",
                "Saved/Paks",
                "Content/Paks"
            ];
            foreach (var relative in relativeCandidates)
            {
                var probe = Path.Combine(candidate, relative.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(probe) && HasArchives(probe))
                {
                    return Path.GetFullPath(probe);
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(candidate, "Paks", SearchOption.AllDirectories))
            {
                if (HasArchives(dir))
                {
                    return Path.GetFullPath(dir);
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    private static bool HasArchives(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Any(file =>
            {
                var extension = Path.GetExtension(file);
                return extension.Equals(".pak", Ico) || extension.Equals(".utoc", Ico);
            });
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static DirectoryInfo[] FindExtraArchiveDirectories(string paksDirectory)
    {
        var parent = Directory.GetParent(paksDirectory);
        if (parent is null)
        {
            return [];
        }

        var extras = new List<DirectoryInfo>();
        try
        {
            foreach (var directory in parent.EnumerateDirectories())
            {
                if (string.Equals(directory.FullName, paksDirectory, Ico)) continue;
                if (HasArchives(directory.FullName))
                {
                    extras.Add(directory);
                }
            }
        }
        catch (Exception)
        {
            // ignore and use the main directory only
        }

        return extras.ToArray();
    }

    private static (string? MainKey, List<(string Guid, string Key)> DynamicKeys) LoadKeys(Options options)
    {
        string? mainKey = options.Key;
        var dynamicKeys = new List<(string Guid, string Key)>();
        string? json = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(options.KeysFile))
            {
                json = File.ReadAllText(options.KeysFile);
            }
            else if (!options.NoKeysEndpoint && !string.IsNullOrWhiteSpace(options.KeysEndpoint))
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                json = client.GetStringAsync(options.KeysEndpoint).GetAwaiter().GetResult();
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"warning: could not load AES keys from {(options.KeysFile ?? options.KeysEndpoint)}: {e.Message}");
        }

        if (json is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                if (mainKey is null && root.TryGetProperty("mainKey", out var mainKeyElement) && mainKeyElement.ValueKind == JsonValueKind.String)
                {
                    mainKey = mainKeyElement.GetString();
                }

                if (root.TryGetProperty("dynamicKeys", out var dynamicElement) && dynamicElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in dynamicElement.EnumerateArray())
                    {
                        if (entry.TryGetProperty("guid", out var guid) && entry.TryGetProperty("key", out var key) &&
                            guid.ValueKind == JsonValueKind.String && key.ValueKind == JsonValueKind.String)
                        {
                            dynamicKeys.Add((guid.GetString()!, key.GetString()!));
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"warning: could not parse AES keys: {e.Message}");
            }
        }

        mainKey ??= FallbackMainKey;
        return (mainKey, dynamicKeys);
    }

    private sealed record ExtractionItem(string ObjectPath, string RelativePath, string OutputPath, AtlasSprite? Sprite);

    private sealed record AtlasSprite(float U0X, float U0Y, float U3X, float U3Y, int Width, int Height);

    private sealed class AtlasImporter
    {
        public readonly List<string> Sprites = [];
        public readonly List<string> AtlasPaths = [];
        public readonly Dictionary<int, int> PageBySprite = [];
        public readonly Dictionary<int, AtlasSprite> RectBySprite = [];

        public static AtlasImporter? Parse(UObject obj)
        {
            string json;
            try
            {
                json = JsonConvert.SerializeObject(obj);
            }
            catch (Exception)
            {
                return null;
            }

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Properties", out var properties))
            {
                return null;
            }

            var importer = new AtlasImporter();

            if (properties.TryGetProperty("SoftAtlasTextures", out var atlasElement) && atlasElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in atlasElement.EnumerateArray())
                {
                    var path = ReadPath(entry);
                    if (path is not null)
                    {
                        importer.AtlasPaths.Add(NormalizeAssetPath(path));
                    }
                }
            }

            if (properties.TryGetProperty("Sprites", out var spritesElement) && spritesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in spritesElement.EnumerateArray())
                {
                    importer.Sprites.Add(ReadPath(entry) ?? string.Empty);
                }
            }

            if (properties.TryGetProperty("SpriteAtlasTextureMap", out var pageElement) && pageElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in pageElement.EnumerateArray())
                {
                    if (TryReadIndex(entry, out var index) && entry.TryGetProperty("Value", out var value) && value.TryGetInt32(out var page))
                    {
                        importer.PageBySprite[index] = page;
                    }
                }
            }

            if (properties.TryGetProperty("SpriteInfoMap", out var infoElement) && infoElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in infoElement.EnumerateArray())
                {
                    if (!TryReadIndex(entry, out var index)) continue;
                    if (!entry.TryGetProperty("Value", out var value) || value.ValueKind != JsonValueKind.Object) continue;

                    var u0x = ReadFloat(value, "uv0X");
                    var u0y = ReadFloat(value, "uv0Y");
                    var u3x = ReadFloat(value, "uv3X");
                    var u3y = ReadFloat(value, "uv3Y");
                    if (float.IsNaN(u0x) || float.IsNaN(u0y) || float.IsNaN(u3x) || float.IsNaN(u3y)) continue;

                    importer.RectBySprite[index] = new AtlasSprite(u0x, u0y, u3x, u3y, ReadInt(value, "width"), ReadInt(value, "height"));
                }
            }

            return importer.Sprites.Count > 0 && importer.AtlasPaths.Count > 0 ? importer : null;
        }

        private static string? ReadPath(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                return element.GetString();
            }

            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("AssetPathName", out var path) && path.ValueKind == JsonValueKind.String)
            {
                return path.GetString();
            }

            return null;
        }

        private static bool TryReadIndex(JsonElement entry, out int index)
        {
            index = -1;
            if (!entry.TryGetProperty("Key", out var key)) return false;

            return key.ValueKind switch
            {
                JsonValueKind.String => int.TryParse(key.GetString(), out index),
                JsonValueKind.Number => key.TryGetInt32(out index),
                _ => false
            };
        }

        private static int ReadInt(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;

        private static float ReadFloat(JsonElement element, string name)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
            {
                return value.GetSingle();
            }
            return float.NaN;
        }
    }

    private sealed class Options
    {
        public string? Paks;
        public string? Out;
        public string? Key;
        public string? KeysEndpoint = DefaultKeysEndpoint;
        public string? KeysFile;
        public string? Mappings;
        public bool NoKeysEndpoint;
        public bool NoSplitAtlas;
        public bool Force;
        public bool DryRun;
        public bool ListDirs;
        public bool Quiet;
        public bool Recursive;
        public bool Help;
        public readonly List<string> Directories = [];
        public readonly List<string> Exclude = [];

        public string? Error;

        public bool Parse(string[] args)
        {
            Error = null;
            for (var i = 0; i < args.Length; i++)
            {
                var argument = args[i];

                string? Next(string name)
                {
                    if (i + 1 >= args.Length)
                    {
                        Error = $"error: missing value for {name}";
                        return null;
                    }
                    return args[++i];
                }

                switch (argument)
                {
                    case "--paks":
                        Paks = Next(argument);
                        break;
                    case "--out":
                        Out = Next(argument);
                        break;
                    case "--key":
                        Key = Next(argument);
                        break;
                    case "--keys-endpoint":
                        KeysEndpoint = Next(argument);
                        break;
                    case "--keys-file":
                        KeysFile = Next(argument);
                        break;
                    case "--mappings":
                        Mappings = Next(argument);
                        break;
                    case "--dir":
                    {
                        var value = Next(argument);
                        if (value is not null) Directories.Add(value);
                        break;
                    }
                    case "--exclude":
                    {
                        var value = Next(argument);
                        if (value is not null) Exclude.Add(value);
                        break;
                    }
                    case "--no-keys-endpoint":
                        NoKeysEndpoint = true;
                        break;
                    case "--no-split-atlas":
                        NoSplitAtlas = true;
                        break;
                    case "--force":
                        Force = true;
                        break;
                    case "--dry-run":
                        DryRun = true;
                        break;
                    case "--list-dirs":
                        ListDirs = true;
                        break;
                    case "--quiet":
                        Quiet = true;
                        break;
                    case "--recursive":
                        Recursive = true;
                        break;
                    case "-h":
                    case "--help":
                        Help = true;
                        break;
                    default:
                        Error = $"error: unknown argument: {argument}";
                        return false;
                }

                if (Error is not null)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
