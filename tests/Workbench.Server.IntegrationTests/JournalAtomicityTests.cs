// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class JournalAtomicityTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task PostingCommitsFrozenSourceBalancedJournalReceiptAndPolicyTogether()
    {
        // GIVEN an authorized typed source, complete first-post policy and two General accounts.
        await using var context = await JournalTestContext.OpenAsync(sqlServer);
        await context.CreateGeneralAccountsAsync();
        await context.ConfigureAsync();
        var source = await context.CreateSourceAsync();
        // WHEN the synthetic source command posts and its original request is retried.
        var request = Guid.NewGuid();
        var posted = await context.PostAsync(source, request);
        var replay = await context.PostAsync(source, request);
        // THEN source, event, balanced entry, two lines, freeze and receipt commit exactly once.
        Assert.Equal(posted, replay);
        foreach (var table in new[] { "SourceEvents", "JournalEntries", "PostingReceipts", "PolicyFreezes" })
            Assert.Equal(1, await context.CountAsync(table));
        Assert.Equal(2, await context.CountAsync("JournalLines"));
        await using var check = new SqlCommand("""
            SELECT e.DebitTotal,e.CreditTotal,
                (SELECT COUNT(*) FROM Accounting.SyntheticSources s WHERE s.TenantId=e.TenantId AND s.Id=x.SourceId AND s.FrozenAtUtc IS NOT NULL),
                (SELECT SUM(l.Debit-l.Credit) FROM Accounting.JournalLines l WHERE l.TenantId=e.TenantId AND l.JournalId=e.Id)
            FROM Accounting.JournalEntries e JOIN Accounting.SourceEvents x
                ON x.TenantId=e.TenantId AND x.Id=e.SourceEventId;
            """, context.Connection);
        await using var reader = await check.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(12.34m, reader.GetDecimal(0));
        Assert.Equal(12.34m, reader.GetDecimal(1));
        Assert.Equal(1, reader.GetInt32(2));
        Assert.Equal(0m, reader.GetDecimal(3));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task FaultsRollbackEveryFinancialRecordAndAllowOriginalSourceToRetry(int failpoint)
    {
        // GIVEN an eligible source in a disposable database.
        await using var context = await JournalTestContext.OpenAsync(sqlServer);
        await context.CreateGeneralAccountsAsync();
        await context.ConfigureAsync();
        var source = await context.CreateSourceAsync();
        var request = Guid.NewGuid();
        // WHEN the test adapter faults before or after invoking the production kernel.
        Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() =>
            context.PostAsync(source, request, failpoint: failpoint))).Number);
        // THEN the outer transaction leaves neither financial rows nor a frozen source.
        foreach (var table in new[] { "SourceEvents", "JournalEntries", "JournalLines", "PostingReceipts", "PolicyFreezes" })
            Assert.Equal(0, await context.CountAsync(table));
        await using (var check = new SqlCommand("SELECT COUNT(*) FROM Accounting.SyntheticSources WHERE FrozenAtUtc IS NOT NULL", context.Connection))
            Assert.Equal(0, (int)(await check.ExecuteScalarAsync())!);
        var posted = await context.PostAsync(source, request);
        Assert.NotEqual(Guid.Empty, posted.JournalId);
    }

    [Fact]
    public async Task CallerRollbackRevertsEvenASuccessfulNestedAdapterCall()
    {
        // GIVEN an eligible source and a caller-owned SQL transaction.
        await using var context = await JournalTestContext.OpenAsync(sqlServer);
        await context.CreateGeneralAccountsAsync();
        await context.ConfigureAsync();
        var source = await context.CreateSourceAsync();
        await using var transaction = (SqlTransaction)await context.Connection.BeginTransactionAsync();
        // WHEN the adapter returns inside that transaction and the caller rolls back.
        await using (var command = JournalTestContext.NewPostCommand(source, Guid.NewGuid(), context.Connection,
            JournalTestContext.ActorId, context.SessionId, source.Revision, context.ConfigurationVersion,
            context.DebitAccountVersion, context.CreditAccountVersion, new DateTime(2026, 2, 1)))
        {
            command.Transaction = transaction;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.NotEqual(Guid.Empty, reader.GetGuid(1));
        }
        await transaction.RollbackAsync();
        // THEN the successful inner procedure did not commit the caller's work.
        foreach (var table in new[] { "SourceEvents", "JournalEntries", "JournalLines", "PostingReceipts", "PolicyFreezes" })
            Assert.Equal(0, await context.CountAsync(table));
        Assert.NotEqual(Guid.Empty, (await context.PostAsync(source)).JournalId);
    }

    [Fact]
    public async Task ConnectionLossWithOpenCallerTransactionRollsBackEveryJournalWrite()
    {
        // GIVEN a posting command inside an independent caller-owned SQL transaction.
        await using var context = await JournalTestContext.OpenAsync(sqlServer);
        await context.CreateGeneralAccountsAsync();
        await context.ConfigureAsync();
        var source = await context.CreateSourceAsync();
        var connection = await context.OpenSiblingAsync();
        try
        {
            var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            // WHEN the source adapter succeeds but the connection closes before caller commit.
            await using (var command = JournalTestContext.NewPostCommand(source, Guid.NewGuid(), connection,
                JournalTestContext.ActorId, context.SessionId, source.Revision, context.ConfigurationVersion,
                context.DebitAccountVersion, context.CreditAccountVersion, new DateTime(2026, 2, 1)))
            {
                command.Transaction = transaction;
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
            }
        }
        finally
        {
            await connection.DisposeAsync();
        }
        // THEN SQL aborts the uncommitted transaction, including its receipt and source freeze.
        foreach (var table in new[] { "SourceEvents", "JournalEntries", "JournalLines", "PostingReceipts", "PolicyFreezes" })
            Assert.Equal(0, await context.CountAsync(table));
        Assert.NotEqual(Guid.Empty, (await context.PostAsync(source)).JournalId);
    }
}
