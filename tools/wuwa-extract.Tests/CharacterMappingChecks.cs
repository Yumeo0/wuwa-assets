using System.Buffers.Binary;
using System.Text;
using Newtonsoft.Json.Linq;
using WuwaExtract;

internal static class CharacterMappingChecks
{
    internal static void Run()
    {
        const string schema = """
            get Skeleton(){return this.skeleton()}
            get Roles(){return GameUtils.ConvertToArray(this.rolesLength(),this.roles,this)}
            skeleton(t){var x=this.bb.__offset(this.pos,10);return x?this.bb.__string(this.pos+x,t):null}
            rolesLength(){var x=this.bb.__offset(this.pos,14);return x?this.bb.__vector_len(this.pos+x):0}
            """;
        var stringSlot = FlatBufferConfig.FieldOffset(schema, "Skeleton", "__string");
        var vectorSlot = FlatBufferConfig.FieldOffset(schema, "Roles", "__vector_len");
        Check(stringSlot == 10 && vectorSlot == 14, "fields use slots from the shipped reader");
        Check(FlatBufferConfig.FieldOffset(schema.Replace("pos,10", "pos,18"), "Skeleton", "__string") == 18,
            "a schema slot change does not reuse the previous offset");
        Fails(() => FlatBufferConfig.FieldOffset(schema, "Missing", "__string"), "missing schema getters fail");
        Fails(() => FlatBufferConfig.FieldOffset(schema, "Skeleton", "__vector_len"), "field type changes fail");

        var bytes = Buffer();
        var config = new FlatBufferConfig(bytes);
        Check(config.String(stringSlot) == "/Game/Aki/Model.Model-data", "FlatBuffer string references decode");
        Check(config.Int32Vector(vectorSlot).SequenceEqual(new[] { 1209, 1501 }), "FlatBuffer role vectors decode");
        Check(config.String(12) is null && config.Int32Vector(6).Length == 0, "absent FlatBuffer fields stay empty");
        var broken = Buffer();
        BinaryPrimitives.WriteInt32LittleEndian(broken.AsSpan(80), int.MaxValue);
        Fails(() => new FlatBufferConfig(broken).String(stringSlot), "truncated strings fail bounds checks");
        broken = Buffer();
        BinaryPrimitives.WriteInt32LittleEndian(broken.AsSpan(68), 0);
        Fails(() => new FlatBufferConfig(broken).String(stringSlot), "null indirect offsets are rejected");

        var mappings = new SpineCharacters();
        const string path = "/Game/Aki/UI/Portraits/Model.ActualSkeleton-data";
        mappings.Add(path, 1209, "db_role.roleskin[1].FormationSpineSkeletonData");
        mappings.Add(path, 1209, "db_role.roleskin[1].FormationSpineSkeletonData");
        Check(mappings.Find("Client/Content/Aki/UI/Portraits/Model.uasset", "ActualSkeleton-data").Count == 1,
            "identical references deduplicate and match package plus export name");
        Check(mappings.Find("Client/Content/Aki/UI/Portraits/model", "actualskeleton-data").Count == 1,
            "Unreal object references match regardless of case");
        Check(mappings.Find("Client/Content/Aki/UI/Portraits/Model", "Different-data").Count == 0,
            "another export in the same package does not inherit an ID");
        mappings.Add(path, 1501, "another supported role variant");
        Check(mappings.Find("Client/Content/Aki/UI/Portraits/Model", "ActualSkeleton-data")
            .Select(reference => reference.CharacterId).SequenceEqual(new[] { 1209, 1501 }),
            "shared models preserve multiple IDs");
        mappings.Add("0", 1402, "placeholder");
        Check(mappings.Find("Client/Content/Aki/Unknown", "Unknown-data").Count == 0,
            "placeholder and unreferenced models remain unmapped");

        var prefab = JObject.Parse("""
            {"Properties":{"ReferenceAssets":[
              {"ObjectName":"Texture2D'Ignore'","ObjectPath":"Client/Content/Aki/Ignore.0"},
              {"ObjectName":"SpineAtlasAsset'Model-atlas'","ObjectPath":"Client/Content/Aki/Model.2"},
              {"ObjectName":"SpineSkeletonDataAsset'Different-data'","ObjectPath":"Client/Content/Aki/Model.7"}
            ]}}
            """);
        Check(SpineCharacters.ReadPrefabSkeletons(prefab).SequenceEqual(new[] { "/Game/Aki/Model.Different-data" }),
            "prefab links use the skeleton export name rather than its numeric export index");
        Check(SpineExtractor.Category("UI/UIResources/Common/Spine/Portraits/Model/model.skel") == "portrait"
            && SpineExtractor.Category("UI/UIResources/UiLuckdraw/Spine/Character/Model/model.skel") == "gacha"
            && SpineExtractor.Category("UI/UIResources/Common/Spine/QSpine/Model/model.skel") == "chibi",
            "manifest categories distinguish portraits, gacha artwork and chibis");
        Console.WriteLine("All Spine character-mapping checks passed.");
    }

    private static byte[] Buffer()
    {
        var bytes = new byte[192];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 64); // Root table.
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 16); // Vtable length.
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 12); // Table length.
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), 4); // Slot 10: string field.
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 8); // Slot 14: vector field.
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(64), 56); // Root -> vtable.
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(68), 12); // String field -> length.
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(72), 56); // Vector field -> length.
        var text = Encoding.UTF8.GetBytes("/Game/Aki/Model.Model-data");
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(80), text.Length);
        text.CopyTo(bytes.AsSpan(84));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(128), 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(132), 1209);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(136), 1501);
        return bytes;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Fails(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception(message);
    }
}
