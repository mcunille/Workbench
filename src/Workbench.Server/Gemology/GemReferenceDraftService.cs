// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Workbench.Server.Gemology;

public sealed class GemReferenceDraftService
{
    private readonly string _connectionString;
    public GemReferenceDraftService(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    public async Task<GemReferenceDraftResponse> SaveAsync(Guid accountId, Guid sessionId, Guid draftId,
        GemReferenceDraftSaveRequest request, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await GemReferenceCurationSql.LockAsync(connection, transaction, cancellationToken);
        await using var command = GemReferenceCurationSql.Command(connection, transaction, "SaveDraft", accountId, sessionId);
        command.Parameters.AddWithValue("@DraftId", draftId);
        command.Parameters.AddWithValue("@EntryId", request.EntryId);
        command.Parameters.Add("@ContentJson", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(GemReferenceInput.Normalize(request.Content), GemReferenceCurationSql.Json);
        command.Parameters.Add("@ExpectedDraftRowVersion", SqlDbType.VarBinary, -1).Value = (object?)GemReferenceCurationSql.Version(request.ExpectedDraftRowVersion) ?? DBNull.Value;
        command.Parameters.Add("@ExpectedPublishedRowVersion", SqlDbType.VarBinary, -1).Value = (object?)GemReferenceCurationSql.Version(request.ExpectedPublishedRowVersion) ?? DBNull.Value;
        GemReferenceDraftResponse draft;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            draft = GemReferenceCurationSql.Draft(reader);
        }
        var catalog = await GemReferenceCatalog.ReadAsync(connection, transaction, cancellationToken);
        var final = catalog.Where(e => e.Content.Id != draft.EntryId).Select(e => e.Content).Append(draft.Content).ToArray();
        var result = draft with { Errors = GemReferenceReview.Errors(draft.Content, final, DateOnly.FromDateTime(DateTime.UtcNow)) };
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<GemReferenceDraftResponse?> ReadAsync(Guid accountId, Guid sessionId, Guid draftId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return await ReadAsync(connection, null, accountId, sessionId, draftId, cancellationToken);
    }

    internal static async Task<GemReferenceDraftResponse?> ReadAsync(SqlConnection connection, SqlTransaction? transaction,
        Guid accountId, Guid sessionId, Guid draftId, CancellationToken cancellationToken)
    {
        await using var command = GemReferenceCurationSql.Command(connection, transaction, "ReadDraft", accountId, sessionId);
        command.Parameters.AddWithValue("@DraftId", draftId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? GemReferenceCurationSql.Draft(reader) : null;
    }

    public async Task<GemReferenceDraftPage> ListAsync(Guid accountId, Guid sessionId, Guid? after, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = GemReferenceCurationSql.Command(connection, null, "ReadDrafts", accountId, sessionId);
        command.Parameters.AddWithValue("@AfterId", (object?)after ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<GemReferenceDraftResponse>();
        while (await reader.ReadAsync(cancellationToken)) rows.Add(GemReferenceCurationSql.Draft(reader));
        return new(rows.Take(50).ToArray(), rows.Count > 50 ? rows[49].Id.ToString("N") : null);
    }
}
