// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class JournalCorrectionConcurrencyTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task TimedOutCorrectionRollsBackAndTheSameRequestCanRetry()
    {
        // GIVEN an original and an independent transaction owning the accounting lock.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var original = await controls.Journal.PostAsync(await controls.Journal.CreateSourceAsync("300"));
        var before = await controls.HistorySnapshotAsync();
        var request = Guid.NewGuid();
        await using var gate = await JournalConcurrencyTests.AccountingLockGate.OpenAsync(controls.Journal.Application.AdminConnectionString);
        // WHEN the correction reaches the observed SQL barrier and its lock wait times out.
        var pending = controls.CorrectAsync(original.JournalId, new DateOnly(2026, 10, 1), "280", request);
        await gate.WaitForBlockedAsync(1);
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => pending)).Number);
        // THEN the entire transaction rolled back and an identical retry succeeds after the lock is released.
        Assert.Equal(before, await controls.HistorySnapshotAsync());
        await gate.ReleaseAsync();
        Assert.NotEqual(Guid.Empty, (await controls.CorrectAsync(original.JournalId, new DateOnly(2026, 10, 1), "280", request)).CorrectionId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OverlappingCorrectionsProduceOneAtomicGroup(bool sameRequest)
    {
        // GIVEN two independent connections blocked at an observed SQL accounting-lock barrier.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var original = await controls.Journal.PostAsync(await controls.Journal.CreateSourceAsync("300"));
        var request = Guid.NewGuid();
        await using var sibling = await controls.Journal.OpenSiblingAsync();
        await using var gate = await JournalConcurrencyTests.AccountingLockGate.OpenAsync(controls.Journal.Application.AdminConnectionString);
        var first = CaptureAsync(() => controls.CorrectAsync(original.JournalId, new DateOnly(2026, 10, 1), "280", request));
        var second = CaptureAsync(() => controls.CorrectAsync(original.JournalId, new DateOnly(2026, 10, 1), "280",
            sameRequest ? request : Guid.NewGuid(), sibling));
        await gate.WaitForBlockedAsync(2);
        // WHEN both commands are released to compete for the same original.
        await gate.ReleaseAsync();
        var results = await Task.WhenAll(first, second);
        // THEN matching requests replay identically; distinct requests have exactly one conflict and no partial state.
        if (sameRequest)
        {
            Assert.All(results, x => Assert.Null(x.Error));
            Assert.Equal(results[0].Result, results[1].Result);
        }
        else
        {
            Assert.Single(results, x => x.Result is not null);
            Assert.Equal(51009, Assert.Single(results, x => x.Error is not null).Error!.Number);
        }
        Assert.Equal(3, await controls.Journal.CountAsync("JournalEntries"));
        Assert.Equal(1, await controls.Journal.CountAsync("CorrectionGroups"));
        Assert.Equal(1, await controls.Journal.CountAsync("CorrectionReceipts"));
        Assert.Equal(3, await controls.Journal.CountAsync("SyntheticSourceRevisions"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CorrectionAndClosureSerializeInEitherObservedOrder(bool correctionFirst)
    {
        // GIVEN an original and an outer transaction that will retain the first operation's accounting lock.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var journal = controls.Journal;
        var original = await journal.PostAsync(await journal.CreateSourceAsync("300"));
        await using var sibling = await journal.OpenSiblingAsync();
        await using var gate = await JournalConcurrencyTests.AccountingLockGate.OpenAsync(journal.Application.AdminConnectionString);
        await CorrectionAssertions.ScalarAsync(journal.Connection, "BEGIN TRANSACTION");
        try
        {
            // WHEN the selected first operation obtains the observed lock before its opponent begins.
            if (correctionFirst)
            {
                var correcting = controls.CorrectAsync(original.JournalId, new DateOnly(2026, 10, 1), "280");
                await gate.WaitForBlockedAsync(1);
                await gate.ReleaseAsync();
                await correcting;
                var closing = controls.CloseAsync(new DateOnly(2026, 10, 1), connection: sibling);
                await gate.WaitForBlockedAsync(1);
                await CorrectionAssertions.ScalarAsync(journal.Connection, "COMMIT TRANSACTION");
                await closing;
            }
            else
            {
                var closing = controls.CloseAsync(new DateOnly(2026, 10, 1));
                await gate.WaitForBlockedAsync(1);
                await gate.ReleaseAsync();
                await closing;
                var correcting = CaptureAsync(() => controls.CorrectAsync(original.JournalId, new DateOnly(2026, 10, 1), "280", connection: sibling));
                await gate.WaitForBlockedAsync(1);
                await CorrectionAssertions.ScalarAsync(journal.Connection, "COMMIT TRANSACTION");
                Assert.Equal(51009, (await correcting).Error!.Number);
            }
            // THEN correction either commits entirely before close or contributes no entries after close.
            Assert.Equal(correctionFirst ? 3 : 1, await journal.CountAsync("JournalEntries"));
            Assert.Equal(correctionFirst ? 1 : 0, await journal.CountAsync("CorrectionReceipts"));
            Assert.Equal(1, await journal.CountAsync("PeriodClosures"));
        }
        finally { await CorrectionAssertions.ScalarAsync(journal.Connection, "IF @@TRANCOUNT>0 ROLLBACK"); }
    }

    private static async Task<(CorrectionResult? Result, SqlException? Error)> CaptureAsync(Func<Task<CorrectionResult>> action)
    {
        try { return (await action(), null); }
        catch (SqlException error) { return (null, error); }
    }
}
