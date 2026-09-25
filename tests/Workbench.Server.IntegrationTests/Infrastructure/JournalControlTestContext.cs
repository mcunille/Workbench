// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Workbench.Server.IntegrationTests.Infrastructure;

internal sealed record PeriodCloseResult(Guid ClosureId, DateOnly PeriodStart, DateTimeOffset RecordedAtUtc);
internal sealed record CorrectionResult(Guid CorrectionId, Guid ReversalJournalId,
    Guid? ReplacementJournalId, Guid? ReplacementSourceRevision, DateTimeOffset RecordedAtUtc);

internal sealed class JournalControlTestContext : IAsyncDisposable
{
    public JournalTestContext Journal { get; }

    private JournalControlTestContext(JournalTestContext journal) => Journal = journal;

    public static async Task<JournalControlTestContext> OpenAsync(SqlServerFixture fixture,
        int fiscalStartMonth = 1, string startDate = "2026-01-01")
    {
        var journal = await JournalTestContext.OpenAsync(fixture);
        try
        {
            await journal.CreateGeneralAccountsAsync();
            await journal.ConfigureAsync(startDate: startDate, fiscalStartMonth: fiscalStartMonth);
            await using var admin = new SqlConnection(journal.Application.AdminConnectionString);
            await admin.OpenAsync();
            await using var install = new SqlCommand(JournalControlAdapterSql.Install, admin);
            await install.ExecuteNonQueryAsync();
            return new JournalControlTestContext(journal);
        }
        catch
        {
            await journal.DisposeAsync();
            throw;
        }
    }

    public async Task<PeriodCloseResult> CloseAsync(DateOnly month, Guid? requestId = null,
        SqlConnection? connection = null, string reason = "Synthetic reconciliation complete",
        string? evidenceJson = null, Guid? expectedConfigurationVersion = null)
    {
        await using var command = new SqlCommand("EXEC Accounting.CloseSyntheticPeriod @ActorId=@actor,@SessionId=@session,@RequestId=@request,@ExpectedConfigurationVersion=@version,@PeriodStart=@month,@Reason=@reason,@Evidence=@evidence", connection ?? Journal.Connection);
        command.Parameters.AddWithValue("@actor", JournalTestContext.ActorId);
        command.Parameters.AddWithValue("@session", Journal.SessionId);
        command.Parameters.AddWithValue("@request", requestId ?? Guid.NewGuid());
        command.Parameters.AddWithValue("@version", expectedConfigurationVersion ?? Journal.ConfigurationVersion);
        command.Parameters.Add(new SqlParameter("@month", SqlDbType.Date) { Value = month.ToDateTime(TimeOnly.MinValue) });
        command.Parameters.AddWithValue("@reason", reason);
        command.Parameters.AddWithValue("@evidence", (object?)evidenceJson ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Period close returned no row.");
        return new PeriodCloseResult(reader.GetGuid(0), DateOnly.FromDateTime(reader.GetDateTime(1)), reader.GetDateTimeOffset(2));
    }

    public async Task<CorrectionResult> CorrectAsync(Guid originalJournalId, DateOnly postingDate,
        string? replacementAmount, Guid? requestId = null, SqlConnection? connection = null,
        string reason = "Correct synthetic amount", int failpoint = 0,
        Guid? expectedSourceRevision = null, Guid? expectedConfigurationVersion = null,
        Guid? expectedDebitVersion = null, Guid? expectedCreditVersion = null,
        DateOnly? replacementDocumentDate = null, string? evidenceJson = null,
        Guid? actorId = null, Guid? sessionId = null)
    {
        await using var command = new SqlCommand("""
            EXEC Accounting.CorrectSyntheticJournal @ActorId=@actor,@SessionId=@session,@RequestId=@request,
              @OriginalJournalId=@original,@ExpectedConfigurationVersion=@version,@PostingDate=@date,
              @ReplacementAmount=@amount,@Reason=@reason,@Failpoint=@failpoint,@ExpectedSourceRevision=@sourceRevision,
              @ExpectedDebitVersion=@debitVersion,@ExpectedCreditVersion=@creditVersion,
              @ReplacementDocumentDate=@documentDate,@EvidenceOverride=@evidence
            """, connection ?? Journal.Connection);
        command.Parameters.AddWithValue("@actor", actorId ?? JournalTestContext.ActorId);
        command.Parameters.AddWithValue("@session", sessionId ?? Journal.SessionId);
        command.Parameters.AddWithValue("@request", requestId ?? Guid.NewGuid());
        command.Parameters.AddWithValue("@original", originalJournalId);
        command.Parameters.AddWithValue("@version", expectedConfigurationVersion ?? Journal.ConfigurationVersion);
        command.Parameters.Add(new SqlParameter("@date", SqlDbType.Date) { Value = postingDate.ToDateTime(TimeOnly.MinValue) });
        command.Parameters.AddWithValue("@amount", (object?)replacementAmount ?? DBNull.Value);
        command.Parameters.AddWithValue("@reason", reason);
        command.Parameters.AddWithValue("@failpoint", failpoint);
        command.Parameters.AddWithValue("@sourceRevision", (object?)expectedSourceRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("@debitVersion", (object?)expectedDebitVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("@creditVersion", (object?)expectedCreditVersion ?? DBNull.Value);
        command.Parameters.Add(new SqlParameter("@documentDate", SqlDbType.Date) { Value = (object?)replacementDocumentDate?.ToDateTime(TimeOnly.MinValue) ?? DBNull.Value });
        command.Parameters.AddWithValue("@evidence", (object?)evidenceJson ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Correction returned no row.");
        var result = new CorrectionResult(reader.GetGuid(0), reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.GetDateTimeOffset(4));
        // The kernel's row is provisional until the adapter has committed its source revision.
        while (await reader.NextResultAsync()) { }
        return result;
    }

    public async Task<string> HistorySnapshotAsync()
    {
        await using var command = new SqlCommand("""
            SELECT (SELECT * FROM Accounting.SourceEvents ORDER BY Id FOR JSON PATH) SourceEvents,
              (SELECT * FROM Accounting.JournalEntries ORDER BY Id FOR JSON PATH) JournalEntries,
              (SELECT * FROM Accounting.JournalLines ORDER BY JournalId,Ordinal FOR JSON PATH) JournalLines,
              (SELECT * FROM Accounting.PostingReceipts ORDER BY RequestId FOR JSON PATH) PostingReceipts,
              (SELECT * FROM Accounting.Periods ORDER BY PeriodStart FOR JSON PATH) Periods,
              (SELECT * FROM Accounting.PeriodClosures ORDER BY PeriodStart FOR JSON PATH) PeriodClosures,
              (SELECT * FROM Accounting.PeriodCloseReceipts ORDER BY RequestId FOR JSON PATH) PeriodCloseReceipts,
              (SELECT * FROM Accounting.CorrectionGroups ORDER BY Id FOR JSON PATH) CorrectionGroups,
              (SELECT * FROM Accounting.CorrectionReceipts ORDER BY RequestId FOR JSON PATH) CorrectionReceipts,
              (SELECT * FROM Accounting.SyntheticSourceRevisions ORDER BY SourceId,Revision FOR JSON PATH) SourceRevisions
            FOR JSON PATH;
            """, Journal.Connection);
        return await ReadCompleteJsonAsync(command);
    }

    internal static async Task<string> ReadCompleteJsonAsync(SqlCommand command)
    {
        // SQL Server splits top-level FOR JSON into 2,033-character rows; ExecuteScalar reads only the first.
        var json = new StringBuilder();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) json.Append(reader.GetString(0));
        if (json.Length == 0) throw new InvalidOperationException("History snapshot returned no JSON.");
        return json.ToString();
    }

    public ValueTask DisposeAsync() => Journal.DisposeAsync();
}
