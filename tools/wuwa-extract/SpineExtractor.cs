using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WuwaExtract;

internal static class SpineExtractor
{
    private const StringComparison Ico = StringComparison.OrdinalIgnoreCase;
    private const string ContentPrefix = "Client/Content/Aki/";
    private static readonly string[] DefaultDirectories =
    [
        "UI/UIResources/Common/Spine",
        "UI/UIResources/UiLuckdraw/Spine/Character"
    ];

    public static int Extract(IFileProvider provider, string output, string? gameVersion,
        IReadOnlyCollection<string> directories, IReadOnlyCollection<string> excludes,
        bool force, bool dryRun, bool quiet)
    {
        var targets = CollectTargets(directories);
        var excluded = excludes.Select(NormalizeDirectory).ToArray();
        var packages = provider.Files.Keys.Where(path => path.StartsWith(ContentPrefix, Ico)
                && path.EndsWith(".uasset", Ico)
                && targets.Any(target => Within(path[ContentPrefix.Length..], target))
                && !excluded.Any(target => Within(path[ContentPrefix.Length..], target)))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var atlases = new List<Atlas>();
        var skeletons = new List<Skeleton>();
        var failed = 0;
        var written = 0;
        var skipped = 0;
        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine($"Spine: scanning {packages.Length} packages in {string.Join(", ", targets)}");

        foreach (var path in packages)
        {
            try
            {
                // Several packages contain both the atlas and skeleton (and even textures).
                // LoadPackageObject alone only returns the main export and misses the others.
                foreach (var export in provider.LoadPackage(path).GetExports())
                {
                    if (export.ExportType is not ("SpineAtlasAsset" or "SpineSkeletonDataAsset")) continue;
                    var properties = JObject.FromObject(export)["Properties"]
                        ?? throw new InvalidDataException($"{export.Name} has no properties");
                    var directory = path[ContentPrefix.Length..path.LastIndexOf('/')];
                    if (export.ExportType == "SpineAtlasAsset")
                    {
                        var text = properties.Value<string>("rawData")
                            ?? throw new InvalidDataException($"{export.Name} has no atlas text");
                        var pages = ReadPageNames(text);
                        var references = properties["atlasPages"]?.ToObject<TextureReference[]>() ?? [];
                        if (pages.Length == 0 || pages.Length != references.Length)
                            throw new InvalidDataException($"{export.Name}: {pages.Length} atlas pages, {references.Length} texture references");
                        atlases.Add(new Atlas(path, export.Name, directory,
                            directory + "/" + StripSuffix(export.Name, "-atlas") + ".atlas", text, pages, references));
                    }
                    else
                    {
                        var data = properties["rawData"]?.ToObject<byte[]>()
                            ?? throw new InvalidDataException($"{export.Name} has no skeleton data");
                        // A binary skeleton's random hash can also start with '{'.
                        // Recognize its header before attempting JSON detection.
                        var json = false;
                        string version;
                        try { version = ReadVersion(data, false); }
                        catch (InvalidDataException) { json = true; version = ReadVersion(data, true); }
                        var source = properties.Value<string>("skeletonDataFileName") ?? "";
                        skeletons.Add(new Skeleton(path, export.Name, directory,
                            directory + "/" + StripSuffix(export.Name, "-data") + (json ? ".json" : ".skel"), data, version, source));
                    }
                }
            }
            catch (Exception e)
            {
                Fail(path, e);
            }
        }

        if (skeletons.Count == 0)
        {
            Console.Error.WriteLine("error: no Spine skeletons found in the selected directories");
            return 1;
        }

        SpineCharacters characters;
        try { characters = SpineCharacters.Load(provider); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"FAIL Spine character mapping: {e.Message}");
            return 1;
        }

        var models = new List<object>();
        var identified = 0;
        var texturePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var savedAtlases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var skeleton in skeletons)
        {
            try
            {
                var candidates = atlases.Where(atlas => atlas.Directory.Equals(skeleton.Directory, Ico)).ToArray();
                var matching = candidates.Where(atlas =>
                    StripSuffix(atlas.Export, "-atlas").Equals(StripSuffix(skeleton.Export, "-data"), Ico)
                    || Path.GetFileNameWithoutExtension(skeleton.Source.Replace('\\', '/'))
                        .Equals(Path.GetFileNameWithoutExtension(atlas.File), Ico)).ToArray();
                var atlas = matching.Length == 1 ? matching[0] : candidates.Length == 1 ? candidates[0]
                    : throw new InvalidDataException($"cannot unambiguously pair {skeleton.Export} with an atlas ({candidates.Length} candidates)");

                var textures = new List<object>();
                for (var index = 0; index < atlas.Pages.Length; index++)
                {
                    var page = atlas.Pages[index];
                    var reference = atlas.References[index];
                    var textureFile = atlas.Directory + "/" + page;
                    // Validate even on --dry-run and when an output file already exists.
                    OutputPath(textureFile);
                    var dot = reference.ObjectPath.LastIndexOf('.');
                    if (dot <= reference.ObjectPath.LastIndexOf('/'))
                        throw new InvalidDataException($"invalid texture reference: {reference.ObjectPath}");
                    var package = reference.ObjectPath[..dot];
                    var quote = reference.ObjectName.IndexOf('\'');
                    var name = quote < 0 ? reference.ObjectName : reference.ObjectName[(quote + 1)..].TrimEnd('\'');
                    if (texturePaths.TryGetValue(textureFile, out var previousSource))
                    {
                        if (!previousSource.Equals(reference.ObjectPath, Ico))
                            throw new InvalidDataException($"conflicting texture references for {textureFile}");
                    }
                    else
                    {
                        Save(textureFile, () =>
                        {
                            var texture = provider.LoadPackage(package).GetExports().OfType<UTexture>()
                                .SingleOrDefault(texture => texture.Name.Equals(name, StringComparison.Ordinal))
                                ?? throw new InvalidDataException($"texture export {name} missing in {package}");
                            var decoded = texture.Decode(ETexturePlatform.DesktopMobile)
                                ?? throw new InvalidDataException($"texture {reference.ObjectPath} has no decodable mip");
                            return decoded.Encode(ETextureFormat.Png, false, out _);
                        });
                        texturePaths.Add(textureFile, reference.ObjectPath);
                    }
                    textures.Add(new { file = textureFile, source = reference.ObjectPath });
                }
                if (!savedAtlases.Contains(atlas.File))
                {
                    Save(atlas.File, () => Encoding.UTF8.GetBytes(atlas.Text));
                    savedAtlases.Add(atlas.File);
                }
                Save(skeleton.File, () => skeleton.Data);
                var characterReferences = characters.Find(skeleton.Package, skeleton.Export);
                var characterIds = characterReferences.Select(reference => reference.CharacterId).Distinct().Order().ToArray();
                var category = Category(skeleton.File);
                if (characterIds.Length > 0) identified++;
                else if (category is "portrait" or "gacha")
                    Console.Error.WriteLine($"warning: no character ID reference for {category}: {skeleton.File}");
                models.Add(new
                {
                    characterId = characterIds.Length == 1 ? characterIds[0] : (int?)null,
                    characterIds, category,
                    skeleton = skeleton.File, atlas = atlas.File, version = skeleton.Version,
                    source = skeleton.Package, export = skeleton.Export, textures,
                    characterReferences = characterReferences.Select(reference => new
                        { characterId = reference.CharacterId, source = reference.Source }).ToArray()
                });
            }
            catch (Exception e)
            {
                Fail(skeleton.Package + "." + skeleton.Export, e);
            }
        }

        if (!dryRun && failed == 0)
        {
            var manifest = JsonConvert.SerializeObject(new { game = gameVersion, models }, Formatting.Indented) + "\n";
            var manifestPath = OutputPath("SPINE.json");
            Directory.CreateDirectory(output);
            File.WriteAllText(manifestPath, manifest);
        }
        Console.WriteLine($"Spine done in {stopwatch.Elapsed:mm\\:ss}: {models.Count} skeletons, "
            + $"{atlases.Count} atlases, {texturePaths.Count} texture pages; "
            + $"{written} {(dryRun ? "would write" : "written")}, {skipped} skipped, {failed} failed; "
            + $"{identified}/{models.Count} with character IDs");
        return failed == 0 ? 0 : 1;

        string OutputPath(string relative)
        {
            var root = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root, OperatingSystem.IsWindows() ? Ico : StringComparison.Ordinal))
                throw new InvalidDataException($"output path escapes the output directory: {relative}");
            return full;
        }

        void Save(string relative, Func<byte[]> encode)
        {
            var destination = OutputPath(relative);
            if (!force && File.Exists(destination))
            {
                skipped++;
                return;
            }
            if (!dryRun)
            {
                var bytes = encode();
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, bytes);
            }
            written++;
            if (!quiet || dryRun) Console.WriteLine("  " + relative);
        }

        void Fail(string path, Exception e)
        {
            failed++;
            Console.Error.WriteLine($"FAIL {path}: {e.Message}");
        }
    }

    internal static string[] CollectTargets(IReadOnlyCollection<string> directories) =>
        (directories.Count > 0 ? directories : DefaultDirectories).Select(NormalizeDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static string Category(string path)
    {
        if (path.StartsWith("UI/UIResources/Common/Spine/Portraits/", Ico)) return "portrait";
        if (path.StartsWith("UI/UIResources/UiLuckdraw/Spine/Character/", Ico)) return "gacha";
        if (path.StartsWith("UI/UIResources/Common/Spine/Skin/", Ico)) return "skin";
        if (path.StartsWith("UI/UIResources/Common/Spine/QSpine/", Ico)) return "chibi";
        if (path.StartsWith("UI/UIResources/Common/Spine/", Ico)) return "full-body";
        return "other";
    }

    private static string NormalizeDirectory(string path)
    {
        path = path.Replace('\\', '/').Trim().Trim('/');
        foreach (var prefix in new[] { "../../../" + ContentPrefix, ContentPrefix, "Game/Aki/", "Aki/" })
        {
            if (!path.StartsWith(prefix, Ico)) continue;
            path = path[prefix.Length..];
            break;
        }
        if (path.Length == 0 || path.Split('/').Any(part => part is ".." or "."))
            throw new ArgumentException($"invalid asset directory: {path}");
        return path;
    }

    private static bool Within(string path, string directory) => path.StartsWith(directory + "/", Ico);
    private static string StripSuffix(string name, string suffix) => name.EndsWith(suffix, Ico) ? name[..^suffix.Length] : name;

    internal static string[] ReadPageNames(string text)
    {
        var pages = new List<string>();
        var nextIsPage = true;
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) { nextIsPage = true; continue; }
            if (nextIsPage)
            {
                var name = line.Trim();
                if (name.Contains(':') || name.StartsWith('/') || name.Contains('\\')
                    || name.Split('/').Any(part => part is ".." or "."))
                    throw new InvalidDataException($"invalid atlas page name: {name}");
                pages.Add(name);
                nextIsPage = false;
            }
        }
        return pages.ToArray();
    }

    internal static string ReadVersion(byte[] data, bool json)
    {
        if (json)
            return JObject.Parse(Encoding.UTF8.GetString(data))["skeleton"]?.Value<string>("spine")
                ?? throw new InvalidDataException("JSON skeleton has no Spine version");

        // 4.x begins with an eight-byte hash; older versions use a length-prefixed hash string.
        foreach (var fixedHash in new[] { true, false })
        {
            try
            {
                var position = fixedHash ? 8 : 0;
                if (!fixedHash) ReadString();
                var version = ReadString();
                if (Regex.IsMatch(version, @"^\d+\.\d+\.\d+$")) return version;

                string ReadString()
                {
                    var length = 0;
                    for (var shift = 0; shift < 35; shift += 7)
                    {
                        var value = data[position++];
                        length |= (value & 0x7f) << shift;
                        if ((value & 0x80) != 0) continue;
                        if (length < 1 || length > 129 || position + length - 1 > data.Length)
                            throw new InvalidDataException("invalid Spine string");
                        var text = Encoding.UTF8.GetString(data, position, length - 1);
                        position += length - 1;
                        return text;
                    }
                    throw new InvalidDataException("invalid Spine string length");
                }
            }
            catch (Exception e) when (e is IndexOutOfRangeException or InvalidDataException) { }
        }
        throw new InvalidDataException("unrecognized Spine binary header");
    }

    private sealed record TextureReference(string ObjectName, string ObjectPath);
    private sealed record Atlas(string Package, string Export, string Directory, string File,
        string Text, string[] Pages, TextureReference[] References);
    private sealed record Skeleton(string Package, string Export, string Directory, string File,
        byte[] Data, string Version, string Source);
}
