// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using System.Text.Json;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class JournalCorrectionAtomicityTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task HistorySnapshotIncludesTheCompleteJsonAndAllControlTables()
    {
        // GIVEN a successful correction whose complete history exceeds a single SQL JSON result fragment.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var original = await controls.Journal.PostAsync(await controls.Journal.CreateSourceAsync("300"));
        await controls.CorrectAsync(original.JournalId, new DateOnly(2026, 10, 1), "280");
        // WHEN the rollback oracle reads all of the history.
        var snapshot = await controls.HistorySnapshotAsync();
        // THEN it is complete JSON including the final retained-source revision table, not a truncated prefix.
        using var parsed = JsonDocument.Parse(snapshot);
        var row = parsed.RootElement[0];
        Assert.Equal(1, row.GetProperty("CorrectionGroups").GetArrayLength());
        Assert.Equal(1, row.GetProperty("CorrectionReceipts").GetArrayLength());
        Assert.Equal(3, row.GetProperty("SourceRevisions").GetArrayLength());
        Assert.True(snapshot.Length > 4000);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task EveryInjectedAppendFailureRollsBackTheWholeGroup(int failpoint)
    {
        // GIVEN immutable source/journal history before an October correction.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var source = await controls.Journal.CreateSourceAsync("300");
        var original = await controls.Journal.PostAsync(source);
        var before = await controls.HistorySnapshotAsync();
        // WHEN a test-only trigger throws after inverse, replacement, group evidence or group receipt.
        var error = await Assert.ThrowsAsync<SqlException>(() => controls.CorrectAsync(
            original.JournalId, new DateOnly(2026, 10, 1), "280", failpoint: failpoint));
        // THEN neither financial history, retained source revisions nor period materialization survives.
        Assert.Equal(51000, error.Number);
        Assert.Equal(before, await controls.HistorySnapshotAsync());
        Assert.Equal(source.Revision, await CorrectionAssertions.ScalarAsync(controls.Journal.Connection,
            "SELECT Revision FROM Accounting.SyntheticSources WHERE Id=@id", source.Id));
    }

    [Fact]
    public async Task TerminatingConnectionBeforeOuterCommitRollsBackAndLostResponseAfterCommitReplays()
    {
        // GIVEN an original and an independent connection holding an explicit source transaction.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var source = await controls.Journal.CreateSourceAsync("300");
        var original = await controls.Journal.PostAsync(source);
        var before = await controls.HistorySnapshotAsync();
        var request = Guid.NewGuid();
        await using var writer = await controls.Journal.OpenSiblingAsync();
        var spid = (int)(await CorrectionAssertions.ScalarAsync(writer, "SELECT CONVERT(int,@@SPID)"))!;
        await CorrectionAssertions.ScalarAsync(writer, "BEGIN TRANSACTION");
        await controls.CorrectAsync(original.JournalId, new DateOnly(2026, 10, 1), "280", request, writer);
        // WHEN the connection is terminated before its outer transaction commits.
        await CorrectionAssertions.AdminAsync(controls.Journal, $"KILL {spid}");
        // THEN SQL rolls back all rows and the same request may commit on a new connection.
        Assert.Equal(before, await controls.HistorySnapshotAsync());
        _ = await controls.CorrectAsync(original.JournalId, new DateOnly(2026, 10, 1), "280", request);
        var committed = await controls.HistorySnapshotAsync();
        // AND treating that committed response as lost, a fresh-connection retry returns the same durable result.
        await using var retry = await controls.Journal.OpenSiblingAsync();
        var result = await controls.CorrectAsync(original.JournalId, new DateOnly(2026, 10, 1), "280", request, retry);
        Assert.Equal(result.CorrectionId, await CorrectionAssertions.ScalarAsync(controls.Journal.Connection,
            "SELECT CorrectionId FROM Accounting.CorrectionReceipts WHERE RequestId=@id", request));
        Assert.Equal(committed, await controls.HistorySnapshotAsync());
    }

    [Fact]
    public async Task FailedReplacementLeavesOriginalHistoryUnchanged()
    {
        // GIVEN a posted independent synthetic source.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var source = await controls.Journal.CreateSourceAsync("300.00");
        var posted = await controls.Journal.PostAsync(source);
        var before = await controls.HistorySnapshotAsync();
        // WHEN replacement exceeds the configured currency precision.
        await Assert.ThrowsAsync<SqlException>(() => controls.CorrectAsync(
            posted.JournalId, new DateOnly(2026, 10, 1), "280.001"));
        // THEN reversal, replacement, receipts and period creation all rolled back.
        Assert.Equal(before, await controls.HistorySnapshotAsync());
    }
}
