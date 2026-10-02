// Copyright (c) 2026 The White Stag Collection.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workbench.Server.Gemology;

internal sealed record GemReferenceSearch(string? Query, string? MaterialKind, string? Group);
internal sealed record GemReferencePosition(string CommonName, Guid Id);

internal static class GemReferenceCursor
{
    private static readonly JsonSerializerOptions Options = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    private sealed record Continuation(
        [property: JsonRequired] string CommonName, [property: JsonRequired] Guid Id,
        [property: JsonRequired] string? Query, [property: JsonRequired] string? MaterialKind,
        [property: JsonRequired] string? Group);

    internal static string Encode(GemReferencePosition position, GemReferenceSearch search) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Continuation(position.CommonName,
            position.Id, search.Query, search.MaterialKind, search.Group), Options));

    internal static bool TryDecode(string? cursor, GemReferenceSearch search, out GemReferencePosition? position)
    {
        position = null;
        if (cursor is null) return true;
        if (cursor.Length is 0 or > 4096) return false;
        try
        {
            var value = JsonSerializer.Deserialize<Continuation>(Convert.FromBase64String(cursor), Options);
            if (value is null || string.IsNullOrWhiteSpace(value.CommonName) || value.CommonName.Length > 200 ||
                value.CommonName.Any(char.IsControl) || value.Id == Guid.Empty || value.Query != search.Query ||
                value.MaterialKind != search.MaterialKind || value.Group != search.Group) return false;
            position = new(value.CommonName, value.Id);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return false;
        }
    }
}
