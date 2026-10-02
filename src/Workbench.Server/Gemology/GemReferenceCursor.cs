// Copyright (c) 2026 The White Stag Collection.

using System.Text;

namespace Workbench.Server.Gemology;

internal sealed record GemReferenceSearch(string? Query, string? MaterialKind, string? Group);
internal sealed record GemReferencePosition(string CommonName, Guid Id);

internal static class GemReferenceCursor
{
    private static readonly Encoding Encoding = new UTF8Encoding(false, true);

    // Length-prefixed UTF-8 keeps every valid 200-character field within the 4096-character envelope.
    internal static string Encode(GemReferencePosition position, GemReferenceSearch search)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding, leaveOpen: true))
        {
            writer.Write((byte)1);
            writer.Write(position.CommonName);
            writer.Write(position.Id.ToByteArray());
            foreach (var field in new[] { search.Query, search.MaterialKind, search.Group })
            {
                writer.Write(field is not null);
                if (field is not null) writer.Write(field);
            }
        }
        return Convert.ToBase64String(stream.ToArray());
    }

    internal static bool TryDecode(string? cursor, GemReferenceSearch search, out GemReferencePosition? position)
    {
        position = null;
        if (cursor is null) return true;
        if (cursor.Length is 0 or > 4096) return false;
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(cursor));
            using var reader = new BinaryReader(stream, Encoding);
            if (reader.ReadByte() != 1) return false;
            string ReadString()
            {
                var length = reader.Read7BitEncodedInt();
                // A valid 200-UTF-16-unit field needs at most 600 UTF-8 bytes.
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
            var query = ReadField();
            var materialKind = ReadField();
            var group = ReadField();
            if (stream.Position != stream.Length || string.IsNullOrWhiteSpace(commonName) || commonName.Length > 200 ||
                commonName.Any(char.IsControl) || id == Guid.Empty || query != search.Query ||
                materialKind != search.MaterialKind || group != search.Group) return false;
            position = new(commonName, id);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or IOException or DecoderFallbackException)
        {
            return false;
        }
    }
}
