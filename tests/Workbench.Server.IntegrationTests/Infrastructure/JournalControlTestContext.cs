// Copyright (c) 2026 The White Stag Collection.

using System.Data;
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
        string? evidenceJson = null)
    {
        await using var command = new SqlCommand("EXEC Accounting.CloseSyntheticPeriod @ActorId=@actor,@SessionId=@session,@RequestId=@request,@ExpectedConfigurationVersion=@version,@PeriodStart=@month,@Reason=@reason,@Evidence=@evidence", connection ?? Journal.Connection);
        command.Parameters.AddWithValue("@actor", JournalTestContext.ActorId);
        command.Parameters.AddWithValue("@session", Journal.SessionId);
        command.Parameters.AddWithValue("@request", requestId ?? Guid.NewGuid());
        command.Parameters.AddWithValue("@version", Journal.ConfigurationVersion);
        command.Parameters.Add(new SqlParameter("@month", SqlDbType.Date) { Value = month.ToDateTime(TimeOnly.MinValue) });
        command.Parameters.AddWithValue("@reason", reason);
        command.Parameters.AddWithValue("@evidence", (object?)evidenceJson ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Period close returned no row.");
        return new PeriodCloseResult(reader.GetGuid(0), DateOnly.FromDateTime(reader.GetDateTime(1)), reader.GetDateTimeOffset(2));
    }

    public Task<CorrectionResult> CorrectAsync(Guid originalJournalId, DateOnly postingDate,
        string? replacementAmount, Guid? requestId = null, SqlConnection? connection = null,
        string reason = "Correct synthetic amount", int failpoint = 0)
        => throw new NotImplementedException("Task 2 installs the synthetic correction adapter.");

    public async Task<string> HistorySnapshotAsync()
    {
        await using var command = new SqlCommand("""
            SELECT (SELECT * FROM Accounting.SourceEvents ORDER BY Id FOR JSON PATH) SourceEvents,
              (SELECT * FROM Accounting.JournalEntries ORDER BY Id FOR JSON PATH) JournalEntries,
              (SELECT * FROM Accounting.JournalLines ORDER BY JournalId,Ordinal FOR JSON PATH) JournalLines,
              (SELECT * FROM Accounting.PostingReceipts ORDER BY RequestId FOR JSON PATH) PostingReceipts,
              (SELECT * FROM Accounting.Periods ORDER BY PeriodStart FOR JSON PATH) Periods,
              (SELECT * FROM Accounting.PeriodClosures ORDER BY PeriodStart FOR JSON PATH) PeriodClosures,
              (SELECT * FROM Accounting.PeriodCloseReceipts ORDER BY RequestId FOR JSON PATH) PeriodCloseReceipts
            FOR JSON PATH;
            """, Journal.Connection);
        return (string)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Control history snapshot returned no row."));
    }

    public ValueTask DisposeAsync() => Journal.DisposeAsync();
}
