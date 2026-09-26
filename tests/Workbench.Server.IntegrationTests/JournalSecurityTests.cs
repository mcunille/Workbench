// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class JournalSecurityTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task RestrictedPrincipalCannotExecuteKernelOrMutateJournalTables()
    {
        // GIVEN a migrated database and the actual restricted web principal.
        await using var context = await JournalTestContext.OpenAsync(sqlServer);
        // WHEN SQL bypasses the typed source adapter.
        await using (var kernel = new SqlCommand("EXEC Accounting.PostJournal", context.Connection))
            Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => kernel.ExecuteNonQueryAsync())).Number);
        foreach (var periodKernel in new[] { "EnsureOpenPeriod", "ClosePeriod" })
        {
            await using var deniedKernel = new SqlCommand($"EXEC Accounting.[{periodKernel}]", context.Connection);
            Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => deniedKernel.ExecuteNonQueryAsync())).Number);
        }
        // THEN every durable journal table rejects each direct DML verb.
        foreach (var table in new[] { "PolicyFreezes", "SourceEvents", "JournalEntries", "JournalLines", "PostingReceipts",
            "Periods", "PeriodClosures", "PeriodCloseReceipts" })
            foreach (var statement in new[]
            {
            $"INSERT Accounting.[{table}] DEFAULT VALUES",
            $"UPDATE Accounting.[{table}] SET TenantId=TenantId",
            $"DELETE Accounting.[{table}]"
        })
            {
                await using var command = new SqlCommand(statement, context.Connection);
                Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
            }
    }

    [Fact]
    public async Task CurrentAuthorityIsRequiredBeforeReceiptReplay()
    {
        // GIVEN a successful posting receipt owned by an authorized actor.
        await using var context = await JournalTestContext.OpenAsync(sqlServer);
        await context.CreateGeneralAccountsAsync();
        await context.ConfigureAsync();
        var source = await context.CreateSourceAsync();
        var request = Guid.NewGuid();
        await context.PostAsync(source, request);
        // WHEN the accounting role is revoked after the first response.
        await using (var admin = new SqlConnection(context.Application.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var revoke = new SqlCommand("""
                DELETE ur FROM [Identity].[UserRoles] ur JOIN Administration.AccountingRoles ar
                    ON ar.TenantId=ur.TenantId AND ar.RoleId=ur.RoleId
                    WHERE ur.TenantId=@tenant AND ur.UserId=@actor;
                """, admin);
            revoke.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
            revoke.Parameters.AddWithValue("@actor", JournalTestContext.ActorId);
            await revoke.ExecuteNonQueryAsync();
        }
        // THEN even the original identical request cannot replay or append anything.
        Assert.Equal(50903, (await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(source, request))).Number);
        Assert.Equal(1, await context.CountAsync("PostingReceipts"));
        Assert.Equal(1, await context.CountAsync("JournalEntries"));
    }

    [Fact]
    public async Task MissingSessionForeignSourceAndChangedRequestCannotCrossTheBoundary()
    {
        // GIVEN one tenant-scoped source and an authorized session.
        await using var context = await JournalTestContext.OpenAsync(sqlServer);
        await context.CreateGeneralAccountsAsync();
        await context.ConfigureAsync();
        var local = await context.CreateSourceAsync();
        var foreign = await context.CreateSourceAsync(tenantId: JournalTestContext.OtherTenantId);
        // WHEN the session is absent or source belongs to another tenant.
        Assert.Equal(50903, (await Assert.ThrowsAsync<SqlException>(() =>
            context.PostAsync(local, sessionId: Guid.NewGuid()))).Number);
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(foreign))).Number);
        var request = Guid.NewGuid();
        await context.PostAsync(local, request);
        // THEN request ID reuse with different canonical input conflicts without appending.
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() =>
            context.PostAsync(local, request, reference: "different"))).Number);
        Assert.Equal(1, await context.CountAsync("JournalEntries"));
        await using var otherTenant = await context.OpenOtherTenantAsync();
        Assert.Equal(0, await context.CountAsync("JournalEntries", otherTenant));
        Assert.Equal(0, await context.CountAsync("PostingReceipts", otherTenant));
        Assert.Equal(0, await context.CountAsync("Periods", otherTenant));
    }

    [Fact]
    [Trait("Category", "MutationProbe")]
    public async Task RemovingSqlScaleGuardMakesTheSameRejectionAssertionFail()
    {
        // GIVEN a scale-two policy and a typed source with nonzero third fractional digit.
        await using var context = await JournalTestContext.OpenAsync(sqlServer);
        await context.CreateGeneralAccountsAsync();
        await context.ConfigureAsync(scale: 2);
        var source = await context.CreateSourceAsync(amount: "12.3450");
        async Task AssertScaleRejected() => Assert.Equal(51000,
            (await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(source))).Number);
        // WHEN the unchanged assertion runs against the real kernel, then against a disposable clone
        // whose debit and credit scale checks have been removed.
        await AssertScaleRejected();
        await MutateProcedureAsync(context, "PostJournal", definition => definition
            .Replace("OR TRY_CONVERT(decimal(28,4),DebitText)<>ROUND(TRY_CONVERT(decimal(28,4),DebitText),@Scale,1)", "", StringComparison.Ordinal)
            .Replace("OR TRY_CONVERT(decimal(28,4),CreditText)<>ROUND(TRY_CONVERT(decimal(28,4),CreditText),@Scale,1)", "", StringComparison.Ordinal));
        var assertionFailure = await Record.ExceptionAsync(AssertScaleRejected);
        // THEN the original rejection assertion detects the mutation by failing, while the mutant posts.
        Assert.NotNull(assertionFailure);
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(assertionFailure);
        Assert.Equal(1, await context.CountAsync("JournalEntries"));
    }

    [Fact]
    [Trait("Category", "MutationProbe")]
    public async Task RemovingPolicyFreezeGuardMakesTheSameEditAssertionFail()
    {
        // GIVEN a first posting that froze the scale-two policy.
        await using var context = await JournalTestContext.OpenAsync(sqlServer);
        await context.CreateGeneralAccountsAsync();
        await context.ConfigureAsync();
        await context.PostAsync(await context.CreateSourceAsync());
        const string changedPolicy = """{"policies":{"country":"US","region":"CA","currency":"USD","scale":0,"fiscalStartMonth":1,"startApproach":"OpeningBalances","plannedStartDate":"2026-01-01"},"mappings":[],"coverage":[]}""";
        async Task AssertEditRejected() => Assert.Equal(50909,
            (await Assert.ThrowsAsync<SqlException>(() => context.SaveAsync(Guid.NewGuid(), "Configure", changedPolicy,
                expectedVersion: context.ConfigurationVersion))).Number);
        // WHEN the same assertion is run before and after disabling both effective freeze guards
        // in this disposable database.
        await AssertEditRejected();
        await MutateProcedureAsync(context, "Save", definition => definition.Replace(
            "IF EXISTS(SELECT 1 FROM Accounting.Periods p WHERE p.TenantId=@TenantId AND (",
            "IF 1=0 AND EXISTS(SELECT 1 FROM Accounting.Periods p WHERE p.TenantId=@TenantId AND (",
            StringComparison.Ordinal).Replace(
            "IF EXISTS(SELECT 1 FROM Accounting.PolicyFreezes f WHERE f.TenantId=@TenantId AND (",
            "IF 1=0 AND EXISTS(SELECT 1 FROM Accounting.PolicyFreezes f WHERE f.TenantId=@TenantId AND (",
            StringComparison.Ordinal));
        var assertionFailure = await Record.ExceptionAsync(AssertEditRejected);
        // THEN the original frozen-policy assertion fails because the mutated procedure accepts the edit.
        Assert.NotNull(assertionFailure);
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(assertionFailure);
    }

    [Fact]
    [Trait("Category", "MutationProbe")]
    public async Task RemovingUsedAccountArchiveGuardMakesTheSameArchiveAssertionFail()
    {
        // GIVEN an account already referenced by immutable journal lines.
        await using var context = await JournalTestContext.OpenAsync(sqlServer);
        await context.CreateGeneralAccountsAsync();
        await context.ConfigureAsync();
        await context.PostAsync(await context.CreateSourceAsync());
        async Task AssertArchiveRejected() => Assert.Equal(50909,
            (await Assert.ThrowsAsync<SqlException>(() => context.SaveAsync(Guid.NewGuid(), "ArchiveAccount",
                """{"isArchived":true}""", context.DebitAccountId, context.DebitAccountVersion))).Number);
        // WHEN the same assertion is run before and after disabling only the disposable archive guard.
        await AssertArchiveRejected();
        await MutateProcedureAsync(context, "Save", definition => definition.Replace(
            "IF JSON_VALUE(@Payload,'$.isArchived')='true' AND EXISTS(SELECT 1 FROM Accounting.JournalLines WHERE TenantId=@TenantId AND AccountId=@Id)",
            "IF 1=0 AND JSON_VALUE(@Payload,'$.isArchived')='true' AND EXISTS(SELECT 1 FROM Accounting.JournalLines WHERE TenantId=@TenantId AND AccountId=@Id)",
            StringComparison.Ordinal));
        var assertionFailure = await Record.ExceptionAsync(AssertArchiveRejected);
        // THEN the original used-account assertion fails because the mutant archives it.
        Assert.NotNull(assertionFailure);
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(assertionFailure);
    }

    private static async Task MutateProcedureAsync(JournalTestContext context, string procedure,
        Func<string, string> mutate)
    {
        await using var admin = new SqlConnection(context.Application.AdminConnectionString);
        await admin.OpenAsync();
        await using var read = new SqlCommand("SELECT OBJECT_DEFINITION(OBJECT_ID(@procedure))", admin);
        read.Parameters.AddWithValue("@procedure", $"[Accounting].[{procedure}]");
        var definition = (string)(await read.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The disposable procedure was not installed."));
        var altered = mutate(definition);
        Assert.NotEqual(definition, altered);
        altered = altered.Replace("CREATE PROCEDURE", "ALTER PROCEDURE", StringComparison.Ordinal);
        await using var command = new SqlCommand(altered, admin) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync();
    }
}
