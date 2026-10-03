// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Workbench.Server.Persistence;

namespace Workbench.Server.Gemology;

public sealed class GemReferenceEffectiveReadService(WorkbenchDbContext database)
{
    internal async Task<GemReferencePageResponse> BrowseAsync(GemReferenceSearch search, GemReferencePosition? after,
        bool includeArchived, CancellationToken ct)
    {
        var tenantId = database.TenantContext.RequireTenantId();
        await using var transaction = await database.Database.BeginTransactionAsync(ct);
        var connection = (SqlConnection)database.Database.GetDbConnection();
        var sqlTransaction = (SqlTransaction)transaction.GetDbTransaction();
        await LockAsync(connection, sqlTransaction, tenantId, "Shared", ct);
        var snapshot = await ReadSnapshotAsync(connection, sqlTransaction, tenantId, ct);
        // Project once, then delegate literal matching and name/uniqueidentifier ordering to SQL.
        // The scalar candidate payload excludes sources and is catalog-sized; the returned page is bounded.
        var candidates = JsonSerializer.Serialize(snapshot.Where(entry => !entry.Retirement.IsRetired &&
            (includeArchived || !entry.IsArchived)).Select(entry => new
            { entry.Id, entry.Origin, entry.CommonName, entry.MaterialKind, entry.Group, entry.Species, entry.Variety, entry.Aliases }), GemReferenceCurationSql.Json);
        await using var command = new SqlCommand("""
            SELECT TOP(51) e.Id,e.Origin FROM OPENJSON(@candidates) WITH (
                Id uniqueidentifier '$.id', Origin nvarchar(16) '$.origin', CommonName nvarchar(200) '$.commonName',
                MaterialKind nvarchar(32) '$.materialKind', [Group] nvarchar(200) '$.group', Species nvarchar(200) '$.species',
                Variety nvarchar(200) '$.variety', Aliases nvarchar(max) '$.aliases' AS JSON) e
            WHERE (@kind IS NULL OR e.MaterialKind COLLATE Latin1_General_100_BIN2=@kind)
                AND (@group IS NULL OR e.[Group] COLLATE Latin1_General_100_CI_AS=@group)
                AND (@afterName IS NULL OR e.CommonName COLLATE Latin1_General_100_CI_AS>@afterName
                    OR (e.CommonName COLLATE Latin1_General_100_CI_AS=@afterName AND (e.Id>@afterId
                        OR (e.Id=@afterId AND e.Origin COLLATE Latin1_General_100_BIN2>@afterOrigin))))
                AND (@query IS NULL OR CHARINDEX(@query,e.CommonName COLLATE Latin1_General_100_CI_AS)>0
                    OR CHARINDEX(@query,e.[Group] COLLATE Latin1_General_100_CI_AS)>0
                    OR CHARINDEX(@query,e.Species COLLATE Latin1_General_100_CI_AS)>0
                    OR CHARINDEX(@query,e.Variety COLLATE Latin1_General_100_CI_AS)>0
                    OR EXISTS(SELECT 1 FROM OPENJSON(e.Aliases) a WHERE CHARINDEX(@query,a.value COLLATE Latin1_General_100_CI_AS)>0))
            ORDER BY e.CommonName COLLATE Latin1_General_100_CI_AS,e.Id,e.Origin COLLATE Latin1_General_100_BIN2;
            """, connection, sqlTransaction);
        command.Parameters.Add("@candidates", SqlDbType.NVarChar, -1).Value = candidates;
        foreach (var (name, value) in new (string, string?)[] { ("query", search.Query), ("kind", search.MaterialKind),
            ("group", search.Group), ("afterName", after?.CommonName), ("afterOrigin", after?.Origin) })
            command.Parameters.Add("@" + name, SqlDbType.NVarChar, 200).Value = (object?)value ?? DBNull.Value;
        command.Parameters.Add("@afterId", SqlDbType.UniqueIdentifier).Value = (object?)after?.Id ?? DBNull.Value;
        var byIdentity = snapshot.ToDictionary(entry => (entry.Id, entry.Origin));
        var rows = new List<GemReferenceListEntry>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var entry = byIdentity[(reader.GetGuid(0), reader.GetString(1))];
                rows.Add(new(entry.Id, entry.MaterialKind, entry.CommonName, entry.Group, entry.Species, entry.Variety, entry.Layer)
                { Origin = entry.Origin, NeedsReview = entry.NeedsReview, ReviewReasons = entry.ReviewReasons });
            }
        }
        await transaction.CommitAsync(ct);
        return new(rows.Take(50).ToArray(), rows.Count > 50 ? GemReferenceCursor.EncodeEffective(
            new(rows[49].CommonName, rows[49].Id, rows[49].Origin), search, includeArchived) : null);
    }

    public Task<GemReferenceDetailResponse?> DetailAsync(Guid id, CancellationToken ct) => DetailAsync(id, null, ct);

    public async Task<GemReferenceDetailResponse?> DetailAsync(Guid id, string? origin, CancellationToken ct)
    {
        if (origin is not (null or "tenant" or "workbench")) throw new ArgumentException("Invalid reference origin.", nameof(origin));
        var tenantId = database.TenantContext.RequireTenantId();
        await using var transaction = await database.Database.BeginTransactionAsync(ct);
        var connection = (SqlConnection)database.Database.GetDbConnection();
        var sqlTransaction = (SqlTransaction)transaction.GetDbTransaction();
        await LockAsync(connection, sqlTransaction, tenantId, "Shared", ct);
        var snapshot = await ReadSnapshotAsync(connection, sqlTransaction, tenantId, ct);
        await transaction.CommitAsync(ct);
        var matches = snapshot.Where(entry => entry.Id == id);
        return origin is null ? matches.OrderByDescending(entry => entry.Origin == "tenant").FirstOrDefault() :
            matches.SingleOrDefault(entry => entry.Origin == origin);
    }

    // Reads and writes use the same current-source snapshot under publication and tenant locks.
    internal static async Task<IReadOnlyList<GemReferenceDetailResponse>> ReadSnapshotAsync(SqlConnection connection,
        SqlTransaction transaction, Guid tenantId, CancellationToken ct)
    {
        var shared = await GemReferenceCatalog.ReadAsync(connection, transaction, ct);
        var overrides = new Dictionary<Guid, (IReadOnlyDictionary<string, GemReferenceFieldOverride> Choices, string Version)>();
        var additions = new List<GemReferenceDetailResponse>();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await using (var command = new SqlCommand("""
            SELECT EntryId,OverridesJson,RowVersion FROM Gemology.TenantOverrides WHERE TenantId=@tenant;
            SELECT ContentJson,IsArchived,RowVersion FROM Gemology.TenantEntries WHERE TenantId=@tenant;
            """, connection, transaction))
        {
            command.Parameters.Add("@tenant", SqlDbType.UniqueIdentifier).Value = tenantId;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) overrides.Add(reader.GetGuid(0),
                (JsonSerializer.Deserialize<Dictionary<string, GemReferenceFieldOverride>>(reader.GetString(1), GemReferenceCurationSql.Json)!,
                Convert.ToBase64String((byte[])reader[2])));
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct)) additions.Add(GemReferenceEffectiveProjection.ResolveTenant(
                JsonSerializer.Deserialize<GemReferenceContent>(reader.GetString(0), GemReferenceCurationSql.Json)!,
                Convert.ToBase64String((byte[])reader[2]), reader.GetBoolean(1), today));
        }
        var entries = shared.Select(entry =>
        {
            overrides.TryGetValue(entry.Content.Id, out var tenant);
            return GemReferenceEffectiveProjection.Resolve(entry.Content, entry.RowVersion,
                tenant.Choices ?? new Dictionary<string, GemReferenceFieldOverride>(), tenant.Version, today);
        }).Concat(additions).ToArray();
        return FlagDuplicates(entries);
    }

    internal static IReadOnlyList<GemReferenceDetailResponse> FlagDuplicates(IEnumerable<GemReferenceDetailResponse> entries)
    {
        var snapshot = entries.ToArray();
        var duplicates = snapshot.Where(IsVisible).GroupBy(Identity, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).SelectMany(group => group.Select(entry => (entry.Id, entry.Origin))).ToHashSet();
        return snapshot.Select(entry =>
        {
            var reasons = entry.ReviewReasons.Where(reason => reason.Key != "identity").ToDictionary(reason => reason.Key, reason => reason.Value);
            if (duplicates.Contains((entry.Id, entry.Origin))) reasons["identity"] = ["Another visible entry has this effective identity."];
            return entry with { NeedsReview = reasons.Count != 0, ReviewReasons = reasons };
        }).ToArray();
    }

    private static bool IsVisible(GemReferenceDetailResponse entry) => !entry.IsArchived && !entry.Retirement.IsRetired;
    private static string Identity(GemReferenceDetailResponse entry) => Convert.ToHexString(GemReferenceInput.IdentityKey(Content(entry)));
    internal static GemReferenceContent Content(GemReferenceDetailResponse entry) => new(entry.Id, entry.MaterialKind,
        entry.CommonName, entry.Group, entry.Species, entry.Variety, entry.Description, entry.Aliases,
        entry.SourceAssertions.Select(source => new GemReferenceSourceContent(source.Id, source.Field, source.Title,
            source.Publisher, source.Url, source.Citation, source.AccessedOn, source.ReviewedOn)).ToArray(),
        entry.NotableLocality, entry.Retirement.IsRetired, entry.Retirement.Explanation, entry.Retirement.RedirectEntryId);

    internal static async Task LockAsync(SqlConnection connection, SqlTransaction transaction, Guid tenantId,
        string mode, CancellationToken ct)
    {
        if (mode == "Exclusive") await GemReferenceCurationSql.LockAsync(connection, transaction, ct);
        await using var command = new SqlCommand("""
            DECLARE @result int;
            IF @mode=N'Shared'
            BEGIN
                EXEC @result=sys.sp_getapplock @Resource=N'Gemology.Publication',@LockMode=@mode,@LockOwner=N'Transaction',@LockTimeout=10000;
                IF @result<0 THROW 50052,'Reference catalog is busy. Retry the request.',1;
            END;
            EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode=@mode,@LockOwner=N'Transaction',@LockTimeout=10000;
            IF @result<0 THROW 50052,'Tenant reference is busy. Retry the request.',1;
            """, connection, transaction);
        command.Parameters.Add("@mode", SqlDbType.NVarChar, 32).Value = mode;
        command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = "Gemology.Tenant:" + tenantId.ToString("D").ToLowerInvariant();
        await command.ExecuteNonQueryAsync(ct);
    }
}
