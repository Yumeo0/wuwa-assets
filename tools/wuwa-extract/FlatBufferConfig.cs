using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace WuwaExtract;

// The game ships the generated readers alongside its SQLite/FlatBuffer config.
// Resolve fields from those readers so game updates cannot silently shift hard-coded slots.
internal sealed class FlatBufferConfig(byte[] data)
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static int FieldOffset(string schema, string property, string operation)
    {
        var getter = Regex.Match(schema, @"\bget\s+" + Regex.Escape(property)
            + @"\s*\(\s*\)\s*\{(?<body>[^{}]*)\}", RegexOptions.CultureInvariant);
        var method = Regex.Match(getter.Groups["body"].Value, @"\bthis\.(?<name>\w+)\s*\(");
        var reader = Regex.Match(schema, @"(?<![\w.])" + Regex.Escape(method.Groups["name"].Value)
            + @"\([^)]*\)\s*\{(?<body>[^{}]*)\}", RegexOptions.CultureInvariant);
        var body = reader.Groups["body"].Value;
        var offset = Regex.Match(body, @"\.__offset\([^,]+,\s*(?<slot>\d+)\)", RegexOptions.CultureInvariant);
        if (!getter.Success || !method.Success || !reader.Success || !offset.Success
            || !body.Contains("." + operation + "(", StringComparison.Ordinal)
            || !int.TryParse(offset.Groups["slot"].Value, out var slot) || slot < 4 || slot % 2 != 0)
            throw new InvalidDataException($"Cannot resolve {property} from the game's generated config reader");
        return slot;
    }

    internal string? String(int slot)
    {
        var field = Field(slot);
        if (field is null) return null;
        var start = Indirect(field.Value);
        var length = Int32(start);
        var bytes = Slice(checked(start + 4), length);
        if (Slice(checked(start + 4 + length), 1)[0] != 0)
            throw new InvalidDataException("Config string is not null-terminated");
        return Utf8.GetString(bytes);
    }

    internal int[] Int32Vector(int slot)
    {
        var field = Field(slot);
        if (field is null) return [];
        var start = Indirect(field.Value);
        var length = Int32(start);
        var bytes = Slice(checked(start + 4), checked(length * 4));
        var values = new int[length];
        for (var i = 0; i < length; i++) values[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes[(i * 4)..]);
        return values;
    }

    private int? Field(int slot)
    {
        var root = Int32(0);
        var vtable = checked(root - Int32(root));
        var size = UInt16(vtable);
        if (size < 4 || size % 2 != 0 || slot < 4 || slot % 2 != 0)
            throw new InvalidDataException("Invalid config vtable");
        Slice(vtable, size);
        if (slot >= size) return null;
        var offset = UInt16(checked(vtable + slot));
        return offset == 0 ? null : checked(root + offset);
    }

    private int Int32(int position) => BinaryPrimitives.ReadInt32LittleEndian(Slice(position, 4));
    private int UInt16(int position) => BinaryPrimitives.ReadUInt16LittleEndian(Slice(position, 2));

    private int Indirect(int position)
    {
        var offset = Int32(position);
        if (offset <= 0) throw new InvalidDataException("Invalid config field reference");
        return checked(position + offset);
    }

    private ReadOnlySpan<byte> Slice(int position, int length)
    {
        if (position < 0 || length < 0 || position > data.Length - length)
            throw new InvalidDataException("Config field extends beyond its FlatBuffer");
        return data.AsSpan(position, length);
    }
}
