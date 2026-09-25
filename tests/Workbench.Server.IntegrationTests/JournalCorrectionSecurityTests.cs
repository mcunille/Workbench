// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class JournalCorrectionSecurityTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task DifferentCurrentlyAuthorizedActorCannotReplayAnotherActorsCommand()
    {
        // GIVEN a complete correction receipt and a second currently authorized same-tenant actor.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var journal = controls.Journal;
        var original = await journal.PostAsync(await journal.CreateSourceAsync("300"));
        var request = Guid.NewGuid();
        await controls.CorrectAsync(original.JournalId, new DateOnly(2026, 10, 1), "280", request);
        var session = Guid.NewGuid();
        await using var admin = new SqlConnection(journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        await using var grant = new SqlCommand("""
            INSERT [Identity].[UserRoles](TenantId,UserId,RoleId)
              SELECT @tenant,@actor,RoleId FROM Administration.AccountingRoles WHERE TenantId=@tenant AND Kind='Administrator';
            INSERT [Identity].[Sessions](Id,TenantId,UserId,TokenHash,SecurityVersion,CreatedAtUtc,LastSeenAtUtc,IdleExpiresAtUtc,AbsoluteExpiresAtUtc)
              SELECT @session,@tenant,@actor,CRYPT_GEN_RANDOM(32),SecurityVersion,SYSUTCDATETIME(),SYSUTCDATETIME(),
                DATEADD(hour,1,SYSUTCDATETIME()),DATEADD(hour,2,SYSUTCDATETIME()) FROM [Identity].[Users] WHERE Id=@actor;
            """, admin);
        grant.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
        grant.Parameters.AddWithValue("@actor", AuthTestApplication.MemberUserId);
        grant.Parameters.AddWithValue("@session", session);
        await grant.ExecuteNonQueryAsync();
        var before = await controls.HistorySnapshotAsync();
        // WHEN that actor supplies the identical request and canonical command.
        var rejected = await Assert.ThrowsAsync<SqlException>(() => controls.CorrectAsync(original.JournalId,
            new DateOnly(2026, 10, 1), "280", request, actorId: AuthTestApplication.MemberUserId, sessionId: session));
        // THEN receipt ownership conflicts instead of returning another actor's result.
        Assert.Equal(51009, rejected.Number);
        Assert.Equal(before, await controls.HistorySnapshotAsync());
    }

    [Fact]
    [Trait("Category", "MutationProbe")]
    public async Task ExactInverseAssertionDetectsUnswappedSqlAmounts()
    {
        // GIVEN an inverse assertion that passes for the real restricted correction kernel.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var journal = controls.Journal;
        var original = await journal.PostAsync(await journal.CreateSourceAsync("300"));
        var correct = await controls.CorrectAsync(original.JournalId, new DateOnly(2026, 10, 1), null);
        await CorrectionAssertions.ExactInverseAsync(controls, original.JournalId, correct.ReversalJournalId);
        // WHEN only the disposable kernel's debit/credit swap is removed.
        await MutateKernelAsync(journal, sql => sql.Replace(
            "AccountCode,AccountName,AccountType,AccountPurpose,Credit,Debit",
            "AccountCode,AccountName,AccountType,AccountPurpose,Debit,Credit", StringComparison.Ordinal));
        var second = await journal.PostAsync(await journal.CreateSourceAsync("300"));
        var mutant = await controls.CorrectAsync(second.JournalId, new DateOnly(2026, 10, 1), null);
        var assertion = await Record.ExceptionAsync(() => CorrectionAssertions.ExactInverseAsync(controls, second.JournalId, mutant.ReversalJournalId));
        // THEN the unchanged behavioral assertion kills the mutant.
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(assertion);
    }

    [Fact]
    [Trait("Category", "MutationProbe")]
    public async Task KernelAuthorityAssertionDetectsPermissionCheckRemovalBeforeReplay()
    {
        // GIVEN a trusted command receipt and a subsequently revoked source permission.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var journal = controls.Journal;
        var original = await journal.PostAsync(await journal.CreateSourceAsync("300"));
        var request = Guid.NewGuid();
        await CorrectionAssertions.KernelAsync(controls, original.JournalId, request: request);
        await CorrectionAssertions.AdminAsync(journal,
            "DELETE ur FROM [Identity].[UserRoles] ur JOIN Administration.AccountingRoles ar ON ar.TenantId=ur.TenantId AND ar.RoleId=ur.RoleId");
        async Task AssertDenied() => Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() =>
            CorrectionAssertions.KernelAsync(controls, original.JournalId, request: request))).Number);
        await AssertDenied();
        // WHEN the disposable kernel no longer checks its trusted source permission.
        await MutateKernelAsync(journal, sql => sql.Replace(
            "EXEC Accounting.RequirePermission @ActorId,@SessionId,@RequiredPermission;", "PRINT N'';", StringComparison.Ordinal));
        var assertion = await Record.ExceptionAsync(AssertDenied);
        // THEN the same denial assertion fails because the mutant replays without authority.
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(assertion);
    }

    [Fact]
    public async Task KernelAndDurableTablesDenyDirectRuntimeWritesAndOtherTenantSeesNoHistory()
    {
        // GIVEN a committed correction visible to its tenant through a real restricted connection.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var original = await controls.Journal.PostAsync(await controls.Journal.CreateSourceAsync("300"));
        await controls.CorrectAsync(original.JournalId, new DateOnly(2026, 10, 1), "280");
        // WHEN runtime SQL attempts direct execution or writes rather than the trusted adapter.
        await using (var kernel = new SqlCommand("EXEC Accounting.CorrectJournal", controls.Journal.Connection))
            Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => kernel.ExecuteNonQueryAsync())).Number);
        foreach (var table in new[] { "CorrectionGroups", "CorrectionReceipts", "SyntheticSourceRevisions" })
            foreach (var sql in new[] { $"INSERT Accounting.{table} DEFAULT VALUES", $"UPDATE Accounting.{table} SET TenantId=TenantId", $"DELETE Accounting.{table}" })
            {
                await using var command = new SqlCommand(sql, controls.Journal.Connection);
                Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
            }
        // THEN another tenant can neither read the result nor use the original identity.
        await using var other = await controls.Journal.OpenOtherTenantAsync();
        foreach (var table in new[] { "CorrectionGroups", "CorrectionReceipts", "SyntheticSourceRevisions" })
            Assert.Equal(0, await controls.Journal.CountAsync(table, other));
        var error = await Assert.ThrowsAsync<SqlException>(() => controls.CorrectAsync(
            original.JournalId, new DateOnly(2026, 10, 2), null, connection: other));
        Assert.Equal(51003, error.Number);
        Assert.DoesNotContain(original.JournalId.ToString(), error.Message, StringComparison.OrdinalIgnoreCase);
        // AND a currently authorized actor in that other tenant still cannot resolve this tenant's original.
        var otherSession = Guid.NewGuid();
        await using (var admin = new SqlConnection(controls.Journal.Application.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var grant = new SqlCommand("""
                INSERT [Identity].[UserRoles](TenantId,UserId,RoleId)
                  SELECT @tenant,@actor,RoleId FROM Administration.AccountingRoles WHERE TenantId=@tenant AND Kind='Administrator';
                INSERT [Identity].[Sessions](Id,TenantId,UserId,TokenHash,SecurityVersion,CreatedAtUtc,LastSeenAtUtc,IdleExpiresAtUtc,AbsoluteExpiresAtUtc)
                  SELECT @session,@tenant,@actor,CRYPT_GEN_RANDOM(32),SecurityVersion,SYSUTCDATETIME(),SYSUTCDATETIME(),
                    DATEADD(hour,1,SYSUTCDATETIME()),DATEADD(hour,2,SYSUTCDATETIME()) FROM [Identity].[Users] WHERE Id=@actor;
                """, admin);
            grant.Parameters.AddWithValue("@tenant", JournalTestContext.OtherTenantId);
            grant.Parameters.AddWithValue("@actor", AuthTestApplication.OtherTenantUserId);
            grant.Parameters.AddWithValue("@session", otherSession);
            await grant.ExecuteNonQueryAsync();
        }
        var unavailable = await Assert.ThrowsAsync<SqlException>(() => controls.CorrectAsync(
            original.JournalId, new DateOnly(2026, 10, 2), null, connection: other,
            actorId: AuthTestApplication.OtherTenantUserId, sessionId: otherSession));
        Assert.Equal(51004, unavailable.Number);
        Assert.DoesNotContain(original.JournalId.ToString(), unavailable.Message, StringComparison.OrdinalIgnoreCase);
        // AND even a privileged attempt to relink a group to a different tenant violates tenant-qualified foreign keys.
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => CorrectionAssertions.AdminAsync(controls.Journal,
            "UPDATE Accounting.CorrectionGroups SET TenantId='bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'"))).Number);
    }

    [Fact]
    public async Task RevokedAuthorityRejectsEvenACommittedReceiptReplay()
    {
        // GIVEN a successful correction with an immutable complete receipt.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var original = await controls.Journal.PostAsync(await controls.Journal.CreateSourceAsync("300"));
        var request = Guid.NewGuid();
        await controls.CorrectAsync(original.JournalId, new DateOnly(2026, 10, 1), "280", request);
        var before = await controls.HistorySnapshotAsync();
        // WHEN the actor's accounting-role authority is revoked.
        await CorrectionAssertions.AdminAsync(controls.Journal, "DELETE ur FROM [Identity].[UserRoles] ur JOIN Administration.AccountingRoles ar ON ar.TenantId=ur.TenantId AND ar.RoleId=ur.RoleId");
        // THEN the original request cannot replay and the complete history remains unchanged.
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => controls.CorrectAsync(
            original.JournalId, new DateOnly(2026, 10, 1), "280", request))).Number);
        Assert.Equal(before, await controls.HistorySnapshotAsync());
    }

    private static async Task MutateKernelAsync(JournalTestContext journal, Func<string, string> mutate)
    {
        await using var admin = new SqlConnection(journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        var definition = (string)(await CorrectionAssertions.ScalarAsync(admin,
            "SELECT OBJECT_DEFINITION(OBJECT_ID(N'Accounting.CorrectJournal'))"))!;
        var mutated = mutate(definition);
        Assert.NotEqual(definition, mutated);
        await using var command = new SqlCommand(mutated.Replace("CREATE PROCEDURE", "ALTER PROCEDURE", StringComparison.Ordinal), admin);
        await command.ExecuteNonQueryAsync();
    }
}
