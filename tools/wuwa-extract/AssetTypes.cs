namespace WuwaExtract;

internal sealed record TextureCategory(string Description, string[] Directories);

internal static class AssetTypes
{
    private const string Images = "UI/UIResources/Common/Image/";

    public static readonly IReadOnlyDictionary<string, TextureCategory> Textures =
        new Dictionary<string, TextureCategory>(StringComparer.Ordinal)
        {
            ["--characters"] = new("Character cards, portraits and team artwork.",
            [
                Images + "Card", Images + "CardLong", Images + "CardPolygon",
                Images + "IconRoleHead80", Images + "IconRoleHead150", Images + "IconRoleHead175",
                Images + "IconRoleHead256", Images + "IconRoleHeadCircle256", Images + "IconRolePile",
                Images + "IconRolePlayerHead256", Images + "PixActivity", "UI/UIResources/UiTeam/Image"
            ]),
            ["--echoes"] = new("Echo artwork, monster icons and sonata set icons.",
            [
                Images + "IconMonsterGoods160", Images + "IconMonsterGoods80",
                Images + "IconMonsterHead", Images + "IconElementSuit"
            ]),
            ["--weapons"] = new("Weapon artwork and icons.", [Images + "IconWeapon"]),
            ["--skins"] = new("Character, weapon and other cosmetic artwork.",
                [Images + "Skin", Images + "IconWeaponSkin"]),
            ["--gacha"] = new("Static gacha artwork (animated gacha art is in --spine).", [Images + "Luckdraw"]),
            ["--items"] = new("Currency, food, gadgets and upgrade material icons.",
            [
                Images + "IconC", Images + "IconCook", Images + "IconDevice",
                Images + "IconMout", Images + "IconRup", Images + "IconWup"
            ]),
            ["--icons"] = new("UI, attributes, elements, emotes, titles and navigation icons.",
            [
                Images + "Com", Images + "IconAttribute", Images + "IconChatEmoticon",
                Images + "IconElement128", Images + "IconElementAttri", Images + "IconElementAttri128",
                Images + "IconElementRound", Images + "IconForceLogo", Images + "IconLogo",
                Images + "ImgNum", Images + "ImgQuality", Images + "TitleDesc",
                "UI/UIResources/Common/Atlas/TabIcon", "UI/UIResources/UiFunc/Atlas", "UI/UIResources/UiInventory/Atlas"
            ]),
            ["--maps"] = new("World-map tiles, map artwork and map icon atlases.",
                ["UI/UIResources/UiWorldMap/Image", "UI/UIResources/Common/Atlas/WorldMapIcon"])
        };
}
