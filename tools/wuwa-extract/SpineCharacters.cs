using System.Text;
using CUE4Parse.FileProvider;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

namespace WuwaExtract;

internal sealed record SpineCharacterReference(int CharacterId, string Source);

internal sealed class SpineCharacters
{
    private const string Content = "Client/Content/Aki/";
    private readonly Dictionary<string, List<SpineCharacterReference>> references = new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<SpineCharacterReference> Find(string package, string export) =>
        references.TryGetValue(ObjectPath(package, export), out var result)
            ? result.OrderBy(reference => reference.CharacterId).ThenBy(reference => reference.Source, StringComparer.Ordinal).ToArray()
            : [];

    internal void Add(string? objectPath, int characterId, string source)
    {
        if (string.IsNullOrEmpty(objectPath) || !objectPath.StartsWith("/Game/Aki/", StringComparison.OrdinalIgnoreCase)) return;
        if (characterId <= 0) throw new InvalidDataException($"Invalid character ID in {source}");
        var key = Content + objectPath["/Game/Aki/".Length..];
        if (!references.TryGetValue(key, out var entries)) references[key] = entries = [];
        var entry = new SpineCharacterReference(characterId, source);
        if (!entries.Contains(entry)) entries.Add(entry);
    }

    internal static SpineCharacters Load(IFileProvider provider)
    {
        var result = new SpineCharacters();
        using var databases = new ConfigDatabases(provider);
        var skinSchema = Schema("RoleSkin");
        var skeletonSlot = FlatBufferConfig.FieldOffset(skinSchema, "SpineSkeletonData", "__string");
        var portraitSlot = FlatBufferConfig.FieldOffset(skinSchema, "FormationSpineSkeletonData", "__string");
        databases.Read("role", "SELECT Id, RoleId, BinData FROM roleskin ORDER BY Id", row =>
        {
            var config = new FlatBufferConfig((byte[])row[2]);
            var id = row.GetInt32(1);
            var source = $"db_role.roleskin[{row.GetInt32(0)}]";
            result.Add(config.String(skeletonSlot), id, source + ".SpineSkeletonData");
            result.Add(config.String(portraitSlot), id, source + ".FormationSpineSkeletonData");
        });

        var trialSchema = Schema("RoleTrialRoleConfig");
        var trialPortraitSlot = FlatBufferConfig.FieldOffset(trialSchema, "FormationSpineSkeletonData", "__string");
        databases.Read("activity", "SELECT RoleId, BinData FROM roletrialroleconfig ORDER BY RoleId", row =>
            result.Add(new FlatBufferConfig((byte[])row[1]).String(trialPortraitSlot), row.GetInt32(0),
                $"db_activity.roletrialroleconfig[{row.GetInt32(0)}].FormationSpineSkeletonData"));

        var resources = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        databases.Read("ui_resource", "SELECT Id, Path, PcPath FROM uiresource", row =>
            resources.Add(row.GetString(0), new[] { row.GetString(1), row.GetString(2) }
                .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));

        var gachaSchema = Schema("GachaViewInfo");
        var resourceSlot = FlatBufferConfig.FieldOffset(gachaSchema, "SpinePrefabResource", "__string");
        var idsSlot = FlatBufferConfig.FieldOffset(gachaSchema, "ShowIdList", "__vector_len");
        var owners = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        databases.Read("gacha", "SELECT BinData FROM gachaviewinfo", row =>
        {
            var config = new FlatBufferConfig((byte[])row[0]);
            var resource = config.String(resourceSlot);
            if (string.IsNullOrEmpty(resource)) return;
            if (!owners.TryGetValue(resource, out var ids)) owners[resource] = ids = [];
            ids.UnionWith(config.Int32Vector(idsSlot));
        });

        foreach (var (resource, ids) in owners.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (ids.Count != 1)
                throw new InvalidDataException($"Gacha resource {resource} has {ids.Count} character IDs; cannot assign its Spine model unambiguously");
            if (!resources.TryGetValue(resource, out var prefabs) || prefabs.Length == 0)
                throw new InvalidDataException($"Missing UI resource for {resource}");
            foreach (var prefab in prefabs)
            {
                var dot = prefab.LastIndexOf('.');
                if (!prefab.StartsWith("/Game/Aki/", StringComparison.OrdinalIgnoreCase) || dot <= prefab.LastIndexOf('/'))
                    throw new InvalidDataException($"Invalid prefab path: {prefab}");
                var package = Content + prefab["/Game/Aki/".Length..dot];
                var exports = provider.LoadPackage(package).GetExports().Where(export => export.ExportType == "PrefabAsset");
                var skeletons = exports.SelectMany(export => ReadPrefabSkeletons(JObject.FromObject(export))).ToArray();
                if (skeletons.Length == 0) throw new InvalidDataException($"No Spine skeleton references in {prefab}");
                foreach (var skeleton in skeletons)
                    result.Add(skeleton, ids.Single(), $"db_gacha.gachaviewinfo.SpinePrefabResource:{resource} -> {prefab}");
            }
        }
        return result;

        string Schema(string name) => Encoding.UTF8.GetString(provider.SaveAsset(Content + $"JavaScript/Core/Define/Config/{name}.js"));
    }

    internal static IEnumerable<string> ReadPrefabSkeletons(JObject export)
    {
        foreach (var reference in export["Properties"]?["ReferenceAssets"] as JArray ?? [])
        {
            var objectName = reference.Value<string>("ObjectName") ?? "";
            if (!objectName.StartsWith("SpineSkeletonDataAsset'", StringComparison.Ordinal)) continue;
            var path = reference.Value<string>("ObjectPath") ?? "";
            var dot = path.LastIndexOf('.');
            if (!path.StartsWith(Content, StringComparison.OrdinalIgnoreCase) || dot <= path.LastIndexOf('/'))
                throw new InvalidDataException($"Invalid Spine prefab reference: {path}");
            var name = objectName["SpineSkeletonDataAsset'".Length..].TrimEnd('\'');
            // ObjectPath's suffix is an export index, while the config uses the export name.
            yield return "/Game/Aki/" + path[Content.Length..dot] + "." + name;
        }
    }

    private static string ObjectPath(string package, string export) =>
        (package.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ? package[..^7] : package) + "." + export;

    private sealed class ConfigDatabases(IFileProvider provider) : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("wuwa-spine-config-");
        private readonly List<string> files = [];

        public void Read(string database, string query, Action<SqliteDataReader> read)
        {
            var path = Path.Combine(directory.FullName, database + ".db");
            if (!File.Exists(path))
            {
                files.Add(path);
                File.WriteAllBytes(path, provider.SaveAsset(Content + $"ConfigDB/db_{database}.db"));
            }
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = query;
            using var rows = command.ExecuteReader();
            while (rows.Read()) read(rows);
        }

        public void Dispose()
        {
            foreach (var path in files) File.Delete(path);
            directory.Delete();
        }
    }
}
