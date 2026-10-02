// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;

namespace Workbench.Server.Gemology;

internal sealed record GemReferenceCatalogEntry(GemReferenceContent Content, string RowVersion);

internal static class GemReferenceCatalog
{
    internal static async Task<IReadOnlyList<GemReferenceCatalogEntry>> ReadAsync(SqlConnection connection,
        SqlTransaction? transaction, CancellationToken cancellationToken, Guid? entryIdFilter = null)
    {
        // Only the four shared catalog tables are read. No tenant context or tenant table participates.
        await using var command = new SqlCommand("""
            SELECT * FROM Gemology.Entries WHERE @Id IS NULL OR Id=@Id;
            SELECT * FROM Gemology.Aliases WHERE @Id IS NULL OR EntryId=@Id ORDER BY EntryId,Position;
            SELECT * FROM Gemology.SourceAssertions WHERE @Id IS NULL OR EntryId=@Id ORDER BY EntryId,Field,Id;
            SELECT * FROM Gemology.LocalityAssertions WHERE @Id IS NULL OR EntryId=@Id;
            """, connection, transaction);
        command.Parameters.AddWithValue("@Id", (object?)entryIdFilter ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new Dictionary<Guid, GemReferenceCatalogEntry>();
        string? Optional(string name) => reader[name] is string value ? value : null;
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = (Guid)reader["Id"];
            entries[id] = new(new(id, (string)reader["MaterialKind"], (string)reader["CommonName"], Optional("Group"), Optional("Species"),
                Optional("Variety"), Optional("Description"), new List<string>(), new List<GemReferenceSourceContent>(), null,
                (bool)reader["IsRetired"], Optional("RetirementExplanation"), reader["RedirectEntryId"] as Guid?),
                Convert.ToBase64String((byte[])reader["RowVersion"]));
        }
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            ((List<string>)entries[(Guid)reader["EntryId"]].Content.Aliases).Add((string)reader["Name"]);
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            ((List<GemReferenceSourceContent>)entries[(Guid)reader["EntryId"]].Content.Sources).Add(new(
                (Guid)reader["Id"], (string)reader["Field"], (string)reader["Title"], (string)reader["Publisher"], Optional("Url"),
                Optional("Citation"), reader["AccessedOn"] is DateTime accessed ? DateOnly.FromDateTime(accessed) : null,
                DateOnly.FromDateTime((DateTime)reader["ReviewedOn"])));
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = (Guid)reader["EntryId"];
            var entry = entries[id];
            entries[id] = entry with { Content = entry.Content with { NotableLocality = new((string)reader["Place"], (string)reader["Scope"],
                DateOnly.FromDateTime((DateTime)reader["ReviewedOn"]), (Guid)reader["SourceAssertionId"]) } };
        }
        return entries.Values.ToArray();
    }
}
