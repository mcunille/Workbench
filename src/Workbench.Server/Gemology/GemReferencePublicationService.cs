// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Workbench.Server.Gemology;

public sealed class GemReferencePublicationService
{
    private readonly string _connectionString;
    public GemReferencePublicationService(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<GemReferencePublishOutcome> PublishAsync(Guid accountId, Guid sessionId,
        GemReferencePublishRequest request, CancellationToken cancellationToken)
    {
        var selection = CanonicalSelection(request.Drafts);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await GemReferenceCurationSql.LockAsync(connection, transaction, cancellationToken);
            var receipt = await ReceiptAsync(connection, transaction, accountId, sessionId, request.RequestId, cancellationToken);
            if (receipt is { } saved && saved.Selection == selection)
            {
                await transaction.CommitAsync(cancellationToken);
                return saved.Outcome;
            }
            var prepared = await PrepareAsync(connection, transaction, accountId, sessionId, request.Drafts, cancellationToken);
            var code = receipt is not null ? "request_conflict" : prepared.Review.Entries.Any(e => e.IsStale) ? "stale_entry" :
                prepared.Review.Entries.Any(e => e.Errors.Count != 0) ? "validation_failed" : "published";
            var outcome = new GemReferencePublishOutcome(request.RequestId, code, [], prepared.Review.Entries);
            var entries = code == "published" ? prepared.Drafts.Select(d => new
            {
                draftId = d.Id,
                entryId = d.EntryId,
                content = GemReferenceInput.Normalize(d.Content),
                identityKey = Convert.ToHexString(GemReferenceInput.IdentityKey(GemReferenceInput.Normalize(d.Content))),
                aliases = d.Content.Aliases.Select((name, position) => new { position, name, normalizedName = GemReferenceInput.Comparison(name) })
            }).ToArray() : [];
            await using var command = GemReferenceCurationSql.Command(connection, transaction, "PublishDraftBatch", accountId, sessionId);
            command.Parameters.AddWithValue("@RequestId", request.RequestId);
            command.Parameters.Add("@SelectionJson", SqlDbType.NVarChar, -1).Value = selection;
            command.Parameters.Add("@EntriesJson", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(entries, GemReferenceCurationSql.Json);
            command.Parameters.Add("@OutcomeJson", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(outcome, GemReferenceCurationSql.Json);
            command.Parameters.Add("@SummaryJson", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(prepared.Review.Entries.Select(e =>
                new { e.EntryId, fields = e.Changes.Select(c => c.Field).ToArray() }), GemReferenceCurationSql.Json);
            var json = (string)(await command.ExecuteScalarAsync(cancellationToken))!;
            await transaction.CommitAsync(cancellationToken);
            return JsonSerializer.Deserialize<GemReferencePublishOutcome>(json, GemReferenceCurationSql.Json)!;
        }
        catch (SqlException exception) when (exception.Number is not 50041 and not 50042)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            // A failed transaction has no completed receipt. Failure evidence is a separate best-effort transaction.
            await TryAuditFailureAsync(accountId, sessionId, request, selection);
            throw;
        }
    }

    public async Task<GemReferenceReviewResponse> ReviewAsync(Guid accountId, Guid sessionId,
        IReadOnlyList<GemReferenceDraftSelection> selection, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await GemReferenceCurationSql.LockAsync(connection, transaction, cancellationToken);
        var prepared = await PrepareAsync(connection, transaction, accountId, sessionId, selection, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return prepared.Review;
    }

    internal static string CanonicalSelection(IReadOnlyList<GemReferenceDraftSelection> selection) => JsonSerializer.Serialize(
        selection.OrderBy(s => s.DraftId).Select(s => s with { ExpectedDraftRowVersion = Convert.ToBase64String(Convert.FromBase64String(s.ExpectedDraftRowVersion)) }),
        GemReferenceCurationSql.Json);

    private static async Task<(IReadOnlyList<GemReferenceDraftResponse> Drafts, GemReferenceReviewResponse Review)> PrepareAsync(
        SqlConnection connection, SqlTransaction transaction, Guid accountId, Guid sessionId,
        IReadOnlyList<GemReferenceDraftSelection> selection, CancellationToken cancellationToken)
    {
        var drafts = new List<GemReferenceDraftResponse>();
        var missing = new List<GemReferenceEntryReview>();
        foreach (var member in selection)
        {
            var draft = await GemReferenceDraftService.ReadAsync(connection, transaction, accountId, sessionId, member.DraftId, cancellationToken);
            if (draft is null) missing.Add(new(member.DraftId, Guid.Empty, true, [], new Dictionary<string, string[]> { ["draft"] = ["Draft no longer exists."] }));
            else drafts.Add(draft);
        }
        var catalog = await GemReferenceCatalog.ReadAsync(connection, transaction, cancellationToken);
        var duplicatedEntries = drafts.GroupBy(d => d.EntryId).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var changed = drafts.Select(d => d.EntryId).ToHashSet();
        var final = catalog.Where(e => !changed.Contains(e.Content.Id)).Select(e => e.Content)
            .Concat(drafts.GroupBy(d => d.EntryId).Select(g => GemReferenceInput.Normalize(g.First().Content))).ToArray();
        var review = drafts.Select(d =>
        {
            var published = catalog.SingleOrDefault(e => e.Content.Id == d.EntryId);
            var result = GemReferenceReview.Build(d, published?.Content, published?.RowVersion, final, DateOnly.FromDateTime(DateTime.UtcNow));
            var errors = result.Errors.ToDictionary(e => e.Key, e => e.Value);
            if (duplicatedEntries.Contains(d.EntryId)) errors["draft"] = ["Select only one draft per shared entry."];
            return result with { IsStale = result.IsStale || selection.Single(s => s.DraftId == d.Id).ExpectedDraftRowVersion != d.RowVersion, Errors = errors };
        }).Concat(missing).ToArray();
        return (drafts, new(review));
    }

    public async Task<GemReferencePublishOutcome?> ReadAsync(Guid accountId, Guid sessionId, Guid requestId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return (await ReceiptAsync(connection, null, accountId, sessionId, requestId, cancellationToken))?.Outcome;
    }

    private static async Task<(string Selection, GemReferencePublishOutcome Outcome)?> ReceiptAsync(SqlConnection connection,
        SqlTransaction? transaction, Guid accountId, Guid sessionId, Guid requestId, CancellationToken cancellationToken)
    {
        await using var command = GemReferenceCurationSql.Command(connection, transaction, "ReadPublication", accountId, sessionId);
        command.Parameters.AddWithValue("@RequestId", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetString(0),
            JsonSerializer.Deserialize<GemReferencePublishOutcome>(reader.GetString(1), GemReferenceCurationSql.Json)!) : null;
    }

    public async Task<GemReferencePublicationAuditPage> AuditAsync(Guid accountId, Guid sessionId, Guid? after, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = GemReferenceCurationSql.Command(connection, null, "ReadPublicationAudit", accountId, sessionId);
        command.Parameters.AddWithValue("@AfterId", (object?)after ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<GemReferencePublicationAuditResponse>();
        while (await reader.ReadAsync(cancellationToken)) rows.Add(new((Guid)reader["Id"], (Guid)reader["RequestId"], (Guid)reader["AccountId"],
            (string)reader["Outcome"], (string)reader["SummaryJson"], (DateTimeOffset)reader["CreatedAtUtc"]));
        return new(rows.Take(50).ToArray(), rows.Count > 50 ? rows[49].Id.ToString("N") : null);
    }

    private async Task TryAuditFailureAsync(Guid accountId, Guid sessionId, GemReferencePublishRequest request, string selection)
    {
        try
        {
            await using var connection = new SqlConnection(_connectionString);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await connection.OpenAsync(deadline.Token);
            await using var command = GemReferenceCurationSql.Command(connection, null, "PublishDraftBatch", accountId, sessionId);
            command.Parameters.AddWithValue("@RequestId", request.RequestId);
            command.Parameters.AddWithValue("@SelectionJson", selection);
            command.Parameters.AddWithValue("@OutcomeJson", JsonSerializer.Serialize(new GemReferencePublishOutcome(request.RequestId, "infrastructure_failure", [], []), GemReferenceCurationSql.Json));
            command.Parameters.AddWithValue("@EntriesJson", "[]");
            command.Parameters.AddWithValue("@SummaryJson", "[]");
            command.Parameters.AddWithValue("@AuditOnly", true);
            await command.ExecuteNonQueryAsync(deadline.Token);
        }
        catch (Exception error) when (error is SqlException or OperationCanceledException) { /* Original failure remains authoritative. */ }
    }
}
