// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class JournalConcurrencyTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task SameRequestFromIndependentConnectionsReturnsOneJournalAndIdenticalReceipts()
    {
        // GIVEN two restricted SQL connections blocked behind the same tenant accounting lock.
        await using var context = await ReadyAsync();
        var source = await context.CreateSourceAsync();
        var request = Guid.NewGuid();
        await using var second = await context.OpenSiblingAsync();
        await using var gate = await AccountingLockGate.OpenAsync(context.Application.AdminConnectionString);
        // WHEN both connections post one request while the barrier is held.
        var firstTask = CaptureAsync(async () => (JournalTestContext.PostingResult?)await context.PostAsync(source, request));
        var secondTask = CaptureAsync(async () => (JournalTestContext.PostingResult?)await context.PostAsync(source, request, connection: second));
        await gate.WaitForBlockedAsync(2);
        await gate.ReleaseAsync();
        var first = await firstTask;
        var repeated = await secondTask;
        // THEN replay returns the original immutable IDs and only one posting exists.
        Assert.Null(first.Error);
        Assert.Null(repeated.Error);
        Assert.Equal(first.Result, repeated.Result);
        Assert.Equal(1, await context.CountAsync("SourceEvents"));
        Assert.Equal(1, await context.CountAsync("JournalEntries"));
        Assert.Equal(1, await context.CountAsync("PostingReceipts"));
    }

    [Fact]
    public async Task DifferentRequestsForOneSourceIdentityLeaveOneConflictAndNoDuplicateJournal()
    {
        // GIVEN a source identity targeted by two different requests on two connections.
        await using var context = await ReadyAsync();
        var source = await context.CreateSourceAsync();
        await using var second = await context.OpenSiblingAsync();
        await using var gate = await AccountingLockGate.OpenAsync(context.Application.AdminConnectionString);
        // WHEN both commands reach the tenant lock before either may proceed.
        var firstTask = CaptureAsync(async () => (JournalTestContext.PostingResult?)await context.PostAsync(source));
        var secondTask = CaptureAsync(async () => (JournalTestContext.PostingResult?)await context.PostAsync(source, connection: second));
        await gate.WaitForBlockedAsync(2);
        await gate.ReleaseAsync();
        var outcomes = new[] { await firstTask, await secondTask };
        // THEN precisely one request commits and the other identifies an existing source conflict.
        Assert.Single(outcomes, x => x.Error is null);
        Assert.Single(outcomes, x => x.Error?.Number == 51009);
        var posted = outcomes.Single(x => x.Error is null).Result!;
        var conflict = outcomes.Single(x => x.Error?.Number == 51009).Error!;
        Assert.Contains(posted.SourceEventId.ToString("D"), conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await context.CountAsync("JournalEntries"));
        Assert.Equal(1, await context.CountAsync("PostingReceipts"));
    }

    [Fact]
    public async Task FirstPostAndPolicyEditCannotCommitIncompatibleStates()
    {
        // GIVEN a valid pre-post policy and two commands sharing the tenant synchronization domain.
        await using var context = await ReadyAsync();
        var source = await context.CreateSourceAsync();
        await using var second = await context.OpenSiblingAsync();
        await using var gate = await AccountingLockGate.OpenAsync(context.Application.AdminConnectionString);
        const string changedPolicy = """{"policies":{"country":"US","region":"CA","currency":"USD","scale":0,"fiscalStartMonth":1,"startApproach":"OpeningBalances","plannedStartDate":"2026-01-01"},"mappings":[],"coverage":[]}""";
        // WHEN posting races a policy scale edit while both are blocked at the SQL lock.
        var postTask = CaptureAsync(async () => (JournalTestContext.PostingResult?)await context.PostAsync(source));
        var editTask = CaptureAsync(async () =>
        {
            await context.SaveAsync(Guid.NewGuid(), "Configure", changedPolicy,
            expectedVersion: context.ConfigurationVersion, connection: second); return null;
        });
        await gate.WaitForBlockedAsync(2);
        await gate.ReleaseAsync();
        var post = await postTask;
        var edit = await editTask;
        // THEN only one wins: posting freezes the old policy, or posting rejects the new revision.
        Assert.True((post.Error is null) != (edit.Error is null));
        Assert.Contains((post.Error ?? edit.Error)!.Number, new[] { 50909, 51009 });
        Assert.Equal(post.Error is null ? 1 : 0, await context.CountAsync("JournalEntries"));
        Assert.Equal(post.Error is null ? 1 : 0, await context.CountAsync("PolicyFreezes"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PostingAndAccountArchiveCannotBothCommit(bool postFirst)
    {
        // GIVEN a source deriving its debit line from an active General account.
        await using var context = await ReadyAsync();
        var source = await context.CreateSourceAsync();
        await using var second = await context.OpenSiblingAsync();
        await using var gate = await AccountingLockGate.OpenAsync(context.Application.AdminConnectionString);
        // WHEN one command queues at the lock before its competitor on another connection.
        Task<(JournalTestContext.PostingResult? Result, SqlException? Error)> Post() =>
            CaptureAsync(async () => (JournalTestContext.PostingResult?)await context.PostAsync(source));
        Task<(JournalTestContext.PostingResult? Result, SqlException? Error)> Archive() =>
            CaptureAsync(async () =>
            {
                await context.SaveAsync(Guid.NewGuid(), "ArchiveAccount",
                """{"isArchived":true}""", context.DebitAccountId, context.DebitAccountVersion, second); return null;
            });
        var firstTask = postFirst ? Post() : Archive();
        await gate.WaitForBlockedAsync(1);
        var secondTask = postFirst ? Archive() : Post();
        await gate.WaitForBlockedAsync(2);
        await gate.ReleaseAsync();
        var first = await firstTask;
        var secondResult = await secondTask;
        var post = postFirst ? first : secondResult;
        var archive = postFirst ? secondResult : first;
        // THEN archived account history and fresh journal use cannot be committed together.
        Assert.Null(first.Error);
        Assert.NotNull(secondResult.Error);
        Assert.Contains(secondResult.Error.Number, new[] { 50909, 51009 });
        Assert.Equal(postFirst, post.Error is null);
        Assert.Equal(!postFirst, archive.Error is null);
        Assert.Equal(post.Error is null ? 1 : 0, await context.CountAsync("JournalEntries"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AccountRevisionRaceUsesTheLockedVersionAndRetainsHistoricalName(bool postFirst)
    {
        // GIVEN a typed source and a descriptive account edit queued around its posting.
        await using var context = await ReadyAsync();
        var source = await context.CreateSourceAsync();
        await using var second = await context.OpenSiblingAsync();
        await using var gate = await AccountingLockGate.OpenAsync(context.Application.AdminConnectionString);
        Task<(JournalTestContext.PostingResult? Result, SqlException? Error)> Post() =>
            CaptureAsync(async () => (JournalTestContext.PostingResult?)await context.PostAsync(source));
        Task<(JournalTestContext.PostingResult? Result, SqlException? Error)> Edit() =>
            CaptureAsync(async () =>
            {
                await context.SaveAsync(Guid.NewGuid(), "UpdateAccount",
                """{"code":"1910","name":"Synthetic debit renamed","description":"New label"}""",
                context.DebitAccountId, context.DebitAccountVersion, second); return null;
            });
        // WHEN the first command is observed waiting at the tenant lock before the second queues.
        var firstTask = postFirst ? Post() : Edit();
        await gate.WaitForBlockedAsync(1);
        var secondTask = postFirst ? Edit() : Post();
        await gate.WaitForBlockedAsync(2);
        await gate.ReleaseAsync();
        var first = await firstTask;
        var secondResult = await secondTask;
        var post = postFirst ? first : secondResult;
        // THEN an edit first makes the expected account revision stale; a post first snapshots the old name.
        Assert.Null(first.Error);
        if (postFirst)
        {
            Assert.Null(secondResult.Error);
            Assert.Equal(1, await context.CountAsync("JournalEntries"));
            await using var line = new SqlCommand("SELECT AccountName FROM Accounting.JournalLines WHERE AccountId=@account", context.Connection);
            line.Parameters.AddWithValue("@account", context.DebitAccountId);
            Assert.Equal("Synthetic debit", (string)(await line.ExecuteScalarAsync())!);
        }
        else
        {
            Assert.Equal(51009, post.Error?.Number);
            Assert.Equal(0, await context.CountAsync("JournalEntries"));
        }
    }

    private async Task<JournalTestContext> ReadyAsync()
    {
        var context = await JournalTestContext.OpenAsync(sqlServer);
        try
        {
            await context.CreateGeneralAccountsAsync();
            await context.ConfigureAsync();
            return context;
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
    }

    private static async Task<(JournalTestContext.PostingResult? Result, SqlException? Error)> CaptureAsync(
        Func<Task<JournalTestContext.PostingResult?>> action)
    {
        try { return (await action(), null); }
        catch (SqlException exception) { return (null, exception); }
    }

    internal sealed class AccountingLockGate : IAsyncDisposable
    {
        private readonly SqlConnection _connection;
        private readonly SqlTransaction _transaction;
        private readonly int _sessionId;
        private readonly string _adminConnectionString;
        private bool _released;

        private AccountingLockGate(SqlConnection connection, SqlTransaction transaction, int sessionId, string adminConnectionString)
        {
            _connection = connection;
            _transaction = transaction;
            _sessionId = sessionId;
            _adminConnectionString = adminConnectionString;
        }

        public static async Task<AccountingLockGate> OpenAsync(string adminConnectionString)
        {
            var connection = new SqlConnection(adminConnectionString);
            await connection.OpenAsync();
            var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            await using var command = new SqlCommand("""
                DECLARE @result int;
                DECLARE @resource nvarchar(255)=N'Accounting:'+CONVERT(nvarchar(36),@tenant);
                EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=1000;
                IF @result<0 THROW 51009,'Test accounting barrier unavailable.',1;
                SELECT CONVERT(int,@@SPID);
                """, connection, transaction);
            command.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
            var sessionId = (int)(await command.ExecuteScalarAsync())!;
            return new AccountingLockGate(connection, transaction, sessionId, adminConnectionString);
        }

        public async Task WaitForBlockedAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
            while (!timeout.IsCancellationRequested)
            {
                await using var observer = new SqlConnection(_adminConnectionString);
                await observer.OpenAsync(timeout.Token);
                await using var command = new SqlCommand("""
                    SELECT COUNT(*) FROM sys.dm_tran_locks
                    WHERE resource_type='APPLICATION' AND request_status='WAIT'
                      AND request_session_id<>@holder;
                    """, observer);
                command.Parameters.AddWithValue("@holder", _sessionId);
                if ((int)(await command.ExecuteScalarAsync(timeout.Token))! >= count) return;
                await Task.Delay(30, timeout.Token);
            }
            throw new TimeoutException($"Expected {count} SQL requests to be blocked by the accounting lock.");
        }

        public async Task ReleaseAsync()
        {
            if (_released) return;
            await _transaction.CommitAsync();
            _released = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_released) await _transaction.RollbackAsync();
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
