// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Workbench.Server.Administration;
using Workbench.Server.Identity;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ServiceAdminIdentityDatabaseTests(SqlServerFixture sqlServer)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-01T00:00:00Z");

    [Fact]
    public async Task ProvisioningIsUniqueAndIndependentOfTenantIdentity()
    {
        // GIVEN a tenant identity with the same email and the actual operator principal.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var operatorConnection = await database.CreateRoleUserAsync("workbench_operator");
        await new OperatorCommands(operatorConnection, new PasswordHasher<WorkbenchUser>(), TimeProvider.System)
            .BootstrapAsync("Tenant", "admin@example.com", "Correct Horse Battery Staple 1!", default);
        var id = Guid.NewGuid();
        // WHEN provisioning the separate identity and repeating its normalized email.
        Assert.Equal(id, await ProvisionAsync(operatorConnection, id));
        var duplicate = await Assert.ThrowsAsync<SqlException>(() => ProvisionAsync(operatorConnection, Guid.NewGuid()));
        Assert.Contains(duplicate.Number, new[] { 2601, 2627 });
        // THEN exactly one account and audit outcome exist, with no tenant column or tenant foreign key.
        Assert.Equal(1, await ScalarAsync<int>(database.AdminConnectionString, "SELECT COUNT(*) FROM ServiceAdministration.Accounts"));
        Assert.Equal(0, await ScalarAsync<int>(database.AdminConnectionString, "SELECT COUNT(*) FROM sys.columns WHERE object_id IN(OBJECT_ID('ServiceAdministration.Accounts'),OBJECT_ID('ServiceAdministration.Sessions')) AND name='TenantId'"));
        Assert.Equal(0, await ScalarAsync<int>(database.AdminConnectionString, "SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id IN(OBJECT_ID('ServiceAdministration.Accounts'),OBJECT_ID('ServiceAdministration.Sessions')) AND referenced_object_id<>OBJECT_ID('ServiceAdministration.Accounts')"));
        Assert.Equal(1, await ScalarAsync<int>(database.AdminConnectionString, "SELECT COUNT(*) FROM Security.SystemSecurityAuditEvents WHERE Action='service-admin.provisioned' AND Outcome='Succeeded' AND MetadataJson IS NULL"));
    }

    [Theory]
    [InlineData("DisableServiceAdmin", false)]
    [InlineData("ResetServiceAdminPassword", true)]
    [InlineData("RevokeServiceAdminSessions", true)]
    public async Task OperatorChangesInvalidateSessionsAndVerifiedVersions(string operation, bool enabled)
    {
        // GIVEN a verified account and an issued session.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var op = await database.CreateRoleUserAsync("workbench_operator");
        var web = await database.CreateWebUserAsync();
        var account = await ProvisionAsync(op, Guid.NewGuid());
        var session = Guid.NewGuid(); var token = Enumerable.Repeat((byte)7, 32).ToArray();
        Assert.Equal(session, await IssueAsync(web, account, session, token));
        // WHEN the operator changes authority, THEN the token and previously verified version fail closed.
        var sql = $"EXEC Administration.{operation} @AccountId=@account,@Now=@now" + (operation == "ResetServiceAdminPassword" ? ",@PasswordHash=N'replacement-hash'" : "");
        await ScalarAsync<object>(op, sql, ("account", account), ("now", Now));
        Assert.Null(await ResolveAsync(web, token, Now));
        Assert.Null(await IssueAsync(web, account, Guid.NewGuid(), new byte[32]));
        Assert.Equal(2L, await ScalarAsync<long>(database.AdminConnectionString, "SELECT SecurityVersion FROM ServiceAdministration.Accounts WHERE Id=@id", ("id", account)));
        Assert.Equal(enabled, await ScalarAsync<bool>(database.AdminConnectionString, "SELECT IsEnabled FROM ServiceAdministration.Accounts WHERE Id=@id", ("id", account)));
        Assert.Equal(2, await ScalarAsync<int>(database.AdminConnectionString, "SELECT COUNT(*) FROM Security.SystemSecurityAuditEvents WHERE Action LIKE 'service-admin.%' AND Outcome='Succeeded' AND MetadataJson IS NULL"));
        // AND a password reset never silently enables a disabled account.
        if (!enabled)
        {
            await ScalarAsync<object>(op, "EXEC Administration.ResetServiceAdminPassword @AccountId=@id,@PasswordHash=N'reset-hash',@Now=@now", ("id", account), ("now", Now));
            Assert.False(await ScalarAsync<bool>(database.AdminConnectionString, "SELECT IsEnabled FROM ServiceAdministration.Accounts WHERE Id=@id", ("id", account)));
        }
    }

    [Theory]
    [InlineData("active")]
    [InlineData("idle-expired")]
    [InlineData("absolute-expired")]
    [InlineData("revoked")]
    [InlineData("disabled")]
    [InlineData("version")]
    [InlineData("unknown")]
    [InlineData("short")]
    [InlineData("long")]
    public async Task ResolutionRevalidatesAuthorityAndCapsIdleLifetime(string state)
    {
        // GIVEN a session approaching absolute expiry and independent invalid-authority conditions.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var web = await database.CreateWebUserAsync(); var account = await ProvisionAsync(await database.CreateRoleUserAsync("workbench_operator"), Guid.NewGuid());
        var token = Enumerable.Repeat((byte)9, 32).ToArray(); var session = Guid.NewGuid();
        await IssueAsync(web, account, session, token);
        var resolveAt = Now.AddMinutes(4);
        var mutation = state switch
        {
            "idle-expired" => "UPDATE ServiceAdministration.Sessions SET IdleExpiresAtUtc=@now",
            "absolute-expired" => "UPDATE ServiceAdministration.Sessions SET IdleExpiresAtUtc=@now,AbsoluteExpiresAtUtc=@now",
            "revoked" => "UPDATE ServiceAdministration.Sessions SET RevokedAtUtc=@now,RevocationReason=N'Logout'",
            "disabled" => "UPDATE ServiceAdministration.Accounts SET IsEnabled=0",
            "version" => "UPDATE ServiceAdministration.Accounts SET SecurityVersion=2",
            _ => null,
        };
        if (mutation is not null) await ScalarAsync<object>(database.AdminConnectionString, mutation, ("now", resolveAt));
        var candidate = state switch { "unknown" => new byte[32], "short" => token[..31], "long" => token.Concat(new byte[1]).ToArray(), _ => token };
        // WHEN the web principal resolves it, THEN only current authority is returned and no tenant result is exposed.
        var result = await ResolveAsync(web, candidate, resolveAt);
        if (state == "active")
        {
            Assert.Equal(session, result);
            Assert.Equal(Now.AddMinutes(10), await ScalarAsync<DateTimeOffset>(database.AdminConnectionString, "SELECT IdleExpiresAtUtc FROM ServiceAdministration.Sessions"));
        }
        else Assert.Null(result);
    }

    [Fact]
    public async Task LogoutCannotRevokeAnotherAccountsSession()
    {
        // GIVEN two independent service admins.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var op = await database.CreateRoleUserAsync("workbench_operator"); var web = await database.CreateWebUserAsync();
        var first = await ProvisionAsync(op, Guid.NewGuid()); var second = await ProvisionAsync(op, Guid.NewGuid(), "other@example.com");
        var session = Guid.NewGuid(); var token = new byte[32]; await IssueAsync(web, second, session, token);
        // WHEN logout substitutes the other account's session, THEN that session remains valid.
        await ScalarAsync<object>(web, "EXEC ServiceAdministration.RevokeSession @AccountId=@account,@SessionId=@session,@Now=@now", ("account", first), ("session", session), ("now", Now));
        Assert.Equal(session, await ResolveAsync(web, token, Now));
        // AND owner logout ends its authority without a later touch reviving it.
        await ScalarAsync<object>(web, "EXEC ServiceAdministration.RevokeSession @AccountId=@account,@SessionId=@session,@Now=@now", ("account", second), ("session", session), ("now", Now));
        Assert.Null(await ResolveAsync(web, token, Now));
    }

    [Fact]
    public async Task SessionCommandsRejectMalformedAndInconsistentInput()
    {
        // GIVEN an enabled account and the restricted runtime principal.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync(); var web = await database.CreateWebUserAsync();
        var account = await ProvisionAsync(await database.CreateRoleUserAsync("workbench_operator"), Guid.NewGuid());
        // WHEN token size or expiry bounds are invalid, THEN no session is persisted.
        foreach (var token in new[] { new byte[31], new byte[33] })
            Assert.Equal(50040, (await Assert.ThrowsAsync<SqlException>(() => IssueAsync(web, account, Guid.NewGuid(), token))).Number);
        Assert.Equal(50040, (await Assert.ThrowsAsync<SqlException>(() => IssueAsync(web, account, Guid.NewGuid(), new byte[32], Now))).Number);
        Assert.Equal(0, await ScalarAsync<int>(database.AdminConnectionString, "SELECT COUNT(*) FROM ServiceAdministration.Sessions"));
        // AND duplicate token hashes cannot represent two sessions.
        await IssueAsync(web, account, Guid.NewGuid(), new byte[32]);
        Assert.Contains((await Assert.ThrowsAsync<SqlException>(() => IssueAsync(web, account, Guid.NewGuid(), new byte[32]))).Number, new[] { 2601, 2627 });
        // AND durable revocation must carry its bounded reason even if a privileged writer submits NULL.
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => ScalarAsync<object>(database.AdminConnectionString,
            "UPDATE ServiceAdministration.Sessions SET RevokedAtUtc=@now,RevocationReason=NULL", ("now", Now)))).Number);
    }

    [Fact]
    public async Task UpgradeRetainsExistingIdentityAndFinancialRecordsAndGuardsDown()
    {
        // GIVEN authentic tenant and financial evidence on the merged base.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer, "20260928071548_AddSupplierOpenItems");
        await context.PostAsync((await context.CommandAsync("Invoice", cost: "306.60")).ToJsonString());
        var before = await SupplierOpenItemMigrationTests.SnapshotAsync(context);
        var connection = context.Journal.Application.AdminConnectionString;
        var identities = await ScalarAsync<string>(connection, "SELECT CONCAT((SELECT * FROM [Identity].[Users] ORDER BY Id FOR JSON PATH),(SELECT * FROM [Identity].[Sessions] ORDER BY Id FOR JSON PATH),(SELECT * FROM Tenancy.Tenants ORDER BY Id FOR JSON PATH))");
        // WHEN applying this release, THEN tenant, session, financial bytes and previous permissions survive.
        await DatabaseMigrator.MigrateAsync(connection, default);
        Assert.Equal(before, await SupplierOpenItemMigrationTests.SnapshotAsync(context));
        Assert.Equal(identities, await ScalarAsync<string>(connection, "SELECT CONCAT((SELECT * FROM [Identity].[Users] ORDER BY Id FOR JSON PATH),(SELECT * FROM [Identity].[Sessions] ORDER BY Id FOR JSON PATH),(SELECT * FROM Tenancy.Tenants ORDER BY Id FOR JSON PATH))"));
        await ProvisionAsync(connection, Guid.NewGuid());
        Assert.Equal(50020, (await Assert.ThrowsAsync<SqlException>(() => DatabaseMigrator.MigrateToAsync(connection, "20260928071548_AddSupplierOpenItems", default))).Number);
    }

    [Theory]
    [InlineData("DisableServiceAdmin")]
    [InlineData("ResetServiceAdminPassword")]
    [InlineData("RevokeServiceAdminSessions")]
    public async Task SessionIssuanceWaitsForOperatorAuthorityTransaction(string operation)
    {
        // GIVEN an operator change whose outer transaction has not committed.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var op = await database.CreateRoleUserAsync("workbench_operator"); var web = await database.CreateWebUserAsync();
        var account = await ProvisionAsync(op, Guid.NewGuid());
        await using var operatorConnection = new SqlConnection(op); await operatorConnection.OpenAsync();
        await using var transaction = (SqlTransaction)await operatorConnection.BeginTransactionAsync();
        await using var change = new SqlCommand($"EXEC Administration.{operation} @AccountId=@account,@Now=@now" + (operation == "ResetServiceAdminPassword" ? ",@PasswordHash=N'replacement'" : ""), operatorConnection, transaction);
        change.Parameters.AddWithValue("@account", account); change.Parameters.AddWithValue("@now", Now);
        await change.ExecuteNonQueryAsync();
        // WHEN issuance tries the earlier verified version during that transaction, THEN it waits for the account lock.
        var blocked = await Assert.ThrowsAsync<SqlException>(() => ScalarAsync<object>(web,
            "SET LOCK_TIMEOUT 100; EXEC ServiceAdministration.CreateSession @SessionId=@session,@AccountId=@account,@SecurityVersion=1,@TokenHash=@token,@Now=@now,@IdleExpiresAtUtc=@idle,@AbsoluteExpiresAtUtc=@absolute",
            ("session", Guid.NewGuid()), ("account", account), ("token", new byte[32]), ("now", Now), ("idle", Now.AddMinutes(5)), ("absolute", Now.AddMinutes(10))));
        Assert.Equal(1222, blocked.Number);
        await transaction.CommitAsync();
        // AND after commitment the verified stale identity cannot issue any session.
        Assert.Null(await IssueAsync(web, account, Guid.NewGuid(), new byte[32]));
        Assert.Equal(0, await ScalarAsync<int>(database.AdminConnectionString, "SELECT COUNT(*) FROM ServiceAdministration.Sessions"));
    }

    [Theory]
    [InlineData("current", 1)]
    [InlineData("password-changed", 0)]
    [InlineData("version-changed", 0)]
    [InlineData("disabled", 0)]
    public async Task CredentialRehashCannotOverwriteAnOperatorChange(string state, int expected)
    {
        // GIVEN the hash and security version observed before a possible operator change.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync(); var web = await database.CreateWebUserAsync();
        var account = await ProvisionAsync(await database.CreateRoleUserAsync("workbench_operator"), Guid.NewGuid());
        var mutation = state switch
        {
            "password-changed" => "UPDATE ServiceAdministration.Accounts SET PasswordHash=N'operator-hash'",
            "version-changed" => "UPDATE ServiceAdministration.Accounts SET SecurityVersion=2",
            "disabled" => "UPDATE ServiceAdministration.Accounts SET IsEnabled=0",
            _ => null,
        };
        if (mutation is not null) await ScalarAsync<object>(database.AdminConnectionString, mutation);
        // WHEN runtime upgrades that observed hash, THEN only unchanged enabled authority is updated.
        Assert.Equal(expected, await ScalarAsync<int>(web, "EXEC ServiceAdministration.RehashPassword @AccountId=@account,@SecurityVersion=1,@ExpectedPasswordHash=N'synthetic-hash',@PasswordHash=N'upgraded-hash'", ("account", account)));
        Assert.Equal(state == "current" ? "upgraded-hash" : state == "password-changed" ? "operator-hash" : "synthetic-hash", await ScalarAsync<string>(database.AdminConnectionString, "SELECT PasswordHash FROM ServiceAdministration.Accounts"));
    }

    internal static async Task<T?> ScalarAsync<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString); await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue("@" + name, value);
        var result = await command.ExecuteScalarAsync(); return result is null or DBNull ? default : (T)result;
    }

    internal static async Task<Guid> ProvisionAsync(string connection, Guid id, string email = "admin@example.com") =>
        await ScalarAsync<Guid>(connection, "EXEC Administration.ProvisionServiceAdmin @AccountId=@id,@Email=@email,@NormalizedEmail=@normalized,@PasswordHash=N'synthetic-hash',@Now=@now", ("id", id), ("email", email), ("normalized", email.ToUpperInvariant()), ("now", Now));

    private static Task<object?> IssueAsync(string connection, Guid account, Guid session, byte[] token, DateTimeOffset? idle = null) =>
        ScalarAsync<object>(connection, "EXEC ServiceAdministration.CreateSession @SessionId=@session,@AccountId=@account,@SecurityVersion=1,@TokenHash=@token,@Now=@now,@IdleExpiresAtUtc=@idle,@AbsoluteExpiresAtUtc=@absolute", ("session", session), ("account", account), ("token", token), ("now", Now), ("idle", idle ?? Now.AddMinutes(5)), ("absolute", Now.AddMinutes(10)));

    private static async Task<object?> ResolveAsync(string connectionString, byte[] token, DateTimeOffset now)
    {
        await using var connection = new SqlConnection(connectionString); await connection.OpenAsync();
        await using var command = new SqlCommand("EXEC ServiceAdministration.ResolveSession @TokenHash=@token,@Now=@now,@IdleTimeoutSeconds=3600", connection);
        command.Parameters.AddWithValue("@token", token); command.Parameters.AddWithValue("@now", now);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.Equal(new[] { "SessionId", "AccountId", "Email" }, Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
        var result = await reader.ReadAsync() ? reader.GetGuid(0) : (object?)null;
        Assert.False(await reader.NextResultAsync()); return result;
    }
}
