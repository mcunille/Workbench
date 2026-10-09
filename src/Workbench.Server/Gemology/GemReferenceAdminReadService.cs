// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using Microsoft.Data.SqlClient;

namespace Workbench.Server.Gemology;

public sealed class GemReferenceAdminReadService(string connectionString)
{
    public async Task<GemReferencePageResponse> BrowseAsync(string? query, string? materialKind, string? group,
        string? cursor, CancellationToken cancellationToken)
    {
        query = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        group = string.IsNullOrWhiteSpace(group) ? null : group.Trim();
        var search = new GemReferenceSearch(query?.ToUpperInvariant(), materialKind, group?.ToUpperInvariant());
        if (!GemReferenceCursor.TryDecode(cursor, search, out var position)) throw new ArgumentException("Invalid cursor.", nameof(cursor));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT TOP(51) e.Id,e.MaterialKind,e.CommonName,e.[Group],e.Species,e.Variety FROM Gemology.Entries e
            WHERE e.IsRetired=0 AND (@kind IS NULL OR e.MaterialKind COLLATE Latin1_General_100_BIN2=@kind)
                AND (@group IS NULL OR e.[Group] COLLATE Latin1_General_100_CI_AS=@group)
                AND (@afterName IS NULL OR e.CommonName COLLATE Latin1_General_100_CI_AS>@afterName
                    OR (e.CommonName COLLATE Latin1_General_100_CI_AS=@afterName AND e.Id>@afterId))
                AND (@query IS NULL OR CHARINDEX(@query,e.CommonName COLLATE Latin1_General_100_CI_AS)>0
                    OR CHARINDEX(@query,e.[Group] COLLATE Latin1_General_100_CI_AS)>0
                    OR CHARINDEX(@query,e.Species COLLATE Latin1_General_100_CI_AS)>0
                    OR CHARINDEX(@query,e.Variety COLLATE Latin1_General_100_CI_AS)>0
                    OR EXISTS(SELECT 1 FROM Gemology.Aliases a WHERE a.EntryId=e.Id AND CHARINDEX(@query,a.Name COLLATE Latin1_General_100_CI_AS)>0))
            ORDER BY e.CommonName COLLATE Latin1_General_100_CI_AS,e.Id;
            """, connection);
        foreach (var (name, value) in new (string, object?)[] { ("query", query), ("kind", materialKind), ("group", group), ("afterName", position?.CommonName), ("afterId", position?.Id) })
            command.Parameters.AddWithValue("@" + name, value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<GemReferenceListEntry>();
        while (await reader.ReadAsync(cancellationToken)) rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
            reader[3] as string, reader[4] as string, reader[5] as string, "workbenchReference"));
        return new(rows.Take(50).ToArray(), rows.Count > 50 ? GemReferenceCursor.Encode(new(rows[49].CommonName, rows[49].Id), search) : null);
    }

    public async Task<GemReferenceDetailResponse?> DetailAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var entry = (await GemReferenceCatalog.ReadAsync(connection, transaction, cancellationToken, id)).SingleOrDefault();
        await transaction.CommitAsync(cancellationToken);
        if (entry is null) return null;
        var content = entry.Content;
        return new(content.Id, content.MaterialKind, content.CommonName, content.Group, content.Species, content.Variety, "workbenchReference",
            content.Aliases, content.Description, entry.RowVersion, content.Sources.Select(s => new GemReferenceSourceResponse(s.Id, s.Field,
                s.Title, s.Publisher, s.Url, s.Citation, s.AccessedOn, s.ReviewedOn, "workbench")).ToArray(), content.NotableLocality,
            new(content.IsRetired, content.RetirementExplanation, content.RedirectEntryId));
    }
}
