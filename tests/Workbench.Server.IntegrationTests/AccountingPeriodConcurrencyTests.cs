// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class AccountingPeriodConcurrencyTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task PostingFirstCommitsJournalBeforeOverlappingClose()
    {
        // GIVEN a posting transaction queued ahead of close at the tenant accounting lock.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var journal = controls.Journal;
        var source = await journal.CreateSourceAsync();
        await using var closer = await journal.OpenSiblingAsync();
        await using var gate = await JournalConcurrencyTests.AccountingLockGate.OpenAsync(
            journal.Application.AdminConnectionString);
        await ExecuteAsync(journal.Connection, "BEGIN TRANSACTION");
        try
        {
            // WHEN posting obtains the lock and retains its outer transaction, close must wait.
            var posting = journal.PostAsync(source, postingDate: new DateTime(2026, 9, 15));
            await gate.WaitForBlockedAsync(1);
            await gate.ReleaseAsync();
            var posted = await posting;
            var closing = controls.CloseAsync(new DateOnly(2026, 9, 1), connection: closer);
            await gate.WaitForBlockedAsync(1);
            await ExecuteAsync(journal.Connection, "COMMIT TRANSACTION");
            var closed = await closing;
            // THEN the close follows the committed journal and both durable records exist.
            Assert.NotEqual(Guid.Empty, posted.JournalId);
            Assert.NotEqual(Guid.Empty, closed.ClosureId);
            Assert.Equal(1, await journal.CountAsync("JournalEntries"));
            Assert.Equal(1, await journal.CountAsync("PeriodClosures"));
        }
        finally { await RollbackIfOpenAsync(journal.Connection); }
    }

    [Fact]
    public async Task ClosingFirstRejectsOverlappingPostingWithoutPartialRows()
    {
        // GIVEN a close transaction queued ahead of a new posting.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var journal = controls.Journal;
        var source = await journal.CreateSourceAsync();
        await using var poster = await journal.OpenSiblingAsync();
        await using var gate = await JournalConcurrencyTests.AccountingLockGate.OpenAsync(
            journal.Application.AdminConnectionString);
        await ExecuteAsync(journal.Connection, "BEGIN TRANSACTION");
        try
        {
            // WHEN closure obtains the lock and retains its outer transaction, posting must wait.
            var closing = controls.CloseAsync(new DateOnly(2026, 9, 1));
            await gate.WaitForBlockedAsync(1);
            await gate.ReleaseAsync();
            var closed = await closing;
            var posting = journal.PostAsync(source, connection: poster,
                postingDate: new DateTime(2026, 9, 15));
            await gate.WaitForBlockedAsync(1);
            await ExecuteAsync(journal.Connection, "COMMIT TRANSACTION");
            var rejected = await Assert.ThrowsAsync<SqlException>(() => posting);
            // THEN closure wins and posting leaves no financial receipt or journal.
            Assert.NotEqual(Guid.Empty, closed.ClosureId);
            Assert.Equal(51009, rejected.Number);
            Assert.Equal(0, await journal.CountAsync("JournalEntries"));
            Assert.Equal(0, await journal.CountAsync("PostingReceipts"));
            Assert.Equal(1, await journal.CountAsync("PeriodClosures"));
        }
        finally { await RollbackIfOpenAsync(journal.Connection); }
    }

    [Fact]
    public async Task LockTimeoutRollsBackAndSameRequestMayRetry()
    {
        // GIVEN a source waiting behind a transaction-owned accounting lock.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var journal = controls.Journal;
        var source = await journal.CreateSourceAsync();
        var request = Guid.NewGuid();
        await using var gate = await JournalConcurrencyTests.AccountingLockGate.OpenAsync(
            journal.Application.AdminConnectionString);
        // WHEN the post times out at the common lock.
        var posting = journal.PostAsync(source, request, postingDate: new DateTime(2026, 9, 15));
        await gate.WaitForBlockedAsync(1);
        var rejected = await Assert.ThrowsAsync<SqlException>(() => posting);
        // THEN its transaction left no period or financial rows, and the identity can retry.
        Assert.Equal(51009, rejected.Number);
        Assert.Equal(0, await journal.CountAsync("Periods"));
        Assert.Equal(0, await journal.CountAsync("JournalEntries"));
        await gate.ReleaseAsync();
        var posted = await journal.PostAsync(source, request, postingDate: new DateTime(2026, 9, 15));
        Assert.NotEqual(Guid.Empty, posted.JournalId);
        Assert.Equal(1, await journal.CountAsync("Periods"));
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RollbackIfOpenAsync(SqlConnection connection)
        => await ExecuteAsync(connection, "IF @@TRANCOUNT>0 ROLLBACK TRANSACTION");
}
