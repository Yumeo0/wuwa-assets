using WuwaExtract;
using Extractor = WuwaExtract.Program;

var root = Path.Combine(Path.GetTempPath(), "wuwa-extract-tests-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    var unrelated = Path.Combine(root, "Unrelated");
    Directory.CreateDirectory(unrelated);
    File.WriteAllBytes(Path.Combine(unrelated, "existing.png"), []);

    var help = Run();
    Assert(help.Code == 0 && help.Output.Contains("--all") && help.Output.Contains("--spine"), "no arguments show help");
    Assert(!help.Output.Contains("Paks:"), "help does not mount the game");
    Assert(Run("--help").Output == help.Output, "implicit and explicit help agree");
    Assert(Run("--paks", Path.Combine(root, "missing"), "--quiet").Output == help.Output,
        "modifiers without a selection show help without opening the game");
    foreach (var flag in AssetTypes.Textures.Keys)
        Assert(help.Output.Contains(flag), $"help explains {flag}");

    var characters = Targets("--characters");
    Assert(characters.Contains("UI/UIResources/Common/Image/IconRoleHead256"), "character category includes portraits");
    Assert(!characters.Contains("UI/UIResources/Common/Image/IconWeapon") && !characters.Contains("Unrelated"),
        "character selection does not discover other categories or existing output");
    var weapons = Targets("--weapons");
    Assert(Targets("--characters", "--weapons").SetEquals(characters.Union(weapons)), "category flags combine");
    Assert(Targets("--characters", "--characters").SetEquals(characters), "repeated flags do not duplicate targets");

    var textures = Options("--textures");
    Assert(textures.ExtractTextures && !textures.ExtractSpine, "textures do not select Spine");
    Assert(Targets("--textures").Contains("Unrelated"), "full texture mode refreshes existing PNG directories");
    Assert(Targets("--all").SetEquals(Targets("--textures", "--spine")), "all is the union of both formats");
    var all = Options("--all");
    Assert(all.ExtractTextures && all.ExtractSpine, "all executes both formats");
    var spine = Options("--spine");
    Assert(spine.ExtractSpine && !spine.ExtractTextures && Targets("--spine").Count == 0,
        "Spine-only mode has no texture targets");

    const string custom = "UI/UIResources/Common/Image/ImgNum";
    Assert(Targets("--dir", custom).SetEquals([custom]), "a custom directory alone selects only those textures");
    Assert(Targets("--all", "--dir", custom).SetEquals([custom]), "custom roots replace presets and discovery");
    Assert(Options("--all", "--dir", custom).ExtractSpine, "custom roots preserve combined format selection");
    Assert(Targets("--maps", "--exclude", "UI/UIResources/UiWorldMap").SetEquals(["UI/UIResources/Common/Atlas/WorldMapIcon"]),
        "excluding a parent removes descendant roots");
    var exclusions = Options("--maps", "--exclude", "/Game/Aki/UI/UIResources/UiWorldMap/Image/MapTiles");
    Assert(exclusions.IsExcluded("UI/UIResources/UiWorldMap/Image/MapTiles/Tile.uasset"),
        "excluding a child filters assets under a broader selected root");
    Assert(!exclusions.IsExcluded("UI/UIResources/UiWorldMap/Image/MapTiles256/Tile.uasset"),
        "exclusions respect directory boundaries");

    foreach (var invalidArgs in new[] { new[] { "--unknown" }, new[] { "--out" }, new[] { "--dir", "--all" } })
    {
        var options = new Extractor.Options();
        Assert(!options.Parse(invalidArgs) && options.Error is not null, "invalid arguments fail: " + string.Join(" ", invalidArgs));
    }
    Console.WriteLine("All extractor option and target-selection checks passed.");
    CharacterMappingChecks.Run();
}
finally
{
    // This exact temporary path was created above; never operate on a discovered parent.
    var fullRoot = Path.GetFullPath(root);
    if (!fullRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        || !Path.GetFileName(fullRoot).StartsWith("wuwa-extract-tests-", StringComparison.Ordinal))
        throw new InvalidOperationException("Unexpected test directory");
    Directory.Delete(fullRoot, recursive: true);
}

void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

Extractor.Options Options(params string[] args)
{
    var options = new Extractor.Options();
    Assert(options.Parse(args), options.Error ?? "arguments should parse");
    return options;
}

HashSet<string> Targets(params string[] args) => new(Extractor.CollectTargets(root, Options(args)), StringComparer.OrdinalIgnoreCase);

(int Code, string Output) Run(params string[] args)
{
    var original = Console.Out;
    using var output = new StringWriter();
    try
    {
        Console.SetOut(output);
        return (Extractor.Main(args), output.ToString());
    }
    finally { Console.SetOut(original); }
}
