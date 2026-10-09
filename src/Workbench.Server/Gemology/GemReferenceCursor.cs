// Copyright (c) 2026 The White Stag Collection.

using System.Text;

namespace Workbench.Server.Gemology;

internal sealed record GemReferenceSearch(string? Query, string? MaterialKind, string? Group);
internal sealed record GemReferencePosition(string CommonName, Guid Id, string Origin = "workbench");

internal static class GemReferenceCursor
{
    private static readonly Encoding Encoding = new UTF8Encoding(false, true);
    internal static string Encode(GemReferencePosition position, GemReferenceSearch search) => Encode(position, search, null);
    internal static string EncodeEffective(GemReferencePosition position, GemReferenceSearch search, bool includeArchived) => Encode(position, search, includeArchived);

    // v1 stays unchanged for shared admin reads; v2 binds effective origin and archive context.
    private static string Encode(GemReferencePosition position, GemReferenceSearch search, bool? includeArchived)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding, leaveOpen: true))
        {
            writer.Write((byte)(includeArchived is null ? 1 : 2));
            writer.Write(position.CommonName);
            writer.Write(position.Id.ToByteArray());
            foreach (var field in new[] { search.Query?.ToUpperInvariant(), search.MaterialKind, search.Group?.ToUpperInvariant() })
            {
                writer.Write(field is not null);
                if (field is not null) writer.Write(field);
            }
            if (includeArchived is { } archived) { writer.Write(position.Origin); writer.Write(archived); }
        }
        return Convert.ToBase64String(stream.ToArray());
    }
    internal static bool TryDecode(string? cursor, GemReferenceSearch search, out GemReferencePosition? position) =>
        TryDecode(cursor, search, null, out position);
    internal static bool TryDecodeEffective(string? cursor, GemReferenceSearch search, bool includeArchived, out GemReferencePosition? position) =>
        TryDecode(cursor, search, includeArchived, out position);

    private static bool TryDecode(string? cursor, GemReferenceSearch search, bool? includeArchived, out GemReferencePosition? position)
    {
        position = null;
        if (cursor is null) return true;
        if (cursor.Length is 0 or > 4096) return false;
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(cursor));
            using var reader = new BinaryReader(stream, Encoding);
            var version = reader.ReadByte();
            if (version is not (1 or 2) || (version == 2 && includeArchived is null) || (version == 1 && includeArchived == true)) return false;
            string ReadString()
            {
                var length = reader.Read7BitEncodedInt();
                if (length is < 0 or > 600) throw new FormatException();
                var bytes = reader.ReadBytes(length);
                if (bytes.Length != length) throw new EndOfStreamException();
                return Encoding.GetString(bytes);
            }
            var commonName = ReadString();
            var idBytes = reader.ReadBytes(16);
            if (idBytes.Length != 16) return false;
            var id = new Guid(idBytes);
            string? ReadField() => reader.ReadBoolean() ? ReadString() : null;
            var query = ReadField(); var materialKind = ReadField(); var group = ReadField();
            var origin = version == 2 ? ReadString() : "workbench";
            if (version == 2 && reader.ReadBoolean() != includeArchived) return false;
            if (stream.Position != stream.Length || string.IsNullOrWhiteSpace(commonName) || commonName.Length > 200 ||
                commonName.Any(char.IsControl) || id == Guid.Empty || query != search.Query?.ToUpperInvariant() || materialKind != search.MaterialKind ||
                group != search.Group?.ToUpperInvariant() || origin is not ("workbench" or "tenant")) return false;
            position = new(commonName, id, origin);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or IOException or DecoderFallbackException) { return false; }
    }
}
