// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Workbench.Server.Administration;
using Workbench.Server.Identity;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class DatabasePermissionTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task ServiceAdminAuthorityIsLimitedToNamedCommands()
    {
        // GIVEN the actual web, worker and operator database principals.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var web = await database.CreateWebUserAsync();
        var worker = await database.CreateRoleUserAsync("workbench_worker");
        var op = await database.CreateRoleUserAsync("workbench_operator");
        await ServiceAdminIdentityDatabaseTests.ProvisionAsync(op, Guid.NewGuid());
        // WHEN principals attempt direct table access, THEN SQL denies all four operations.
        foreach (var principal in new[] { web, worker, op })
            foreach (var table in new[] { "Accounts", "Sessions" })
                foreach (var query in new[] { $"SELECT * FROM ServiceAdministration.{table}", $"INSERT ServiceAdministration.{table} DEFAULT VALUES", $"UPDATE ServiceAdministration.{table} SET Id=NEWID() WHERE 1=0", $"DELETE ServiceAdministration.{table} WHERE 1=0" })
                    await AssertDeniedAsync(principal, query, 229);
        // AND web/worker cannot invoke operator commands; worker/operator cannot invoke authentication commands.
        foreach (var principal in new[] { web, worker })
            foreach (var command in new[] { "ProvisionServiceAdmin", "DisableServiceAdmin", "ResetServiceAdminPassword", "RevokeServiceAdminSessions" })
                await AssertDeniedAsync(principal, $"EXEC Administration.{command}", 229);
        foreach (var principal in new[] { worker, op })
            foreach (var command in new[] { "FindAccountForLogin", "CreateSession", "ResolveSession", "RevokeSession" })
                await AssertDeniedAsync(principal, $"EXEC ServiceAdministration.{command}", 229);
        // AND credential lookup exposes only the candidate; it neither includes tenant context nor extra results.
        await using var connection = new SqlConnection(web); await connection.OpenAsync();
        await using var lookup = new SqlCommand("EXEC ServiceAdministration.FindAccountForLogin @NormalizedEmail=N'ADMIN@EXAMPLE.COM'", connection);
        await using var reader = await lookup.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(new[] { "Id", "Email", "NormalizedEmail", "PasswordHash", "IsEnabled", "SecurityVersion", "CreatedAtUtc" }, Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
        Assert.False(await reader.ReadAsync()); Assert.False(await reader.NextResultAsync());
    }

    [Fact]
    public async Task ProofProtectedProceduresFailClosedWhenTheProofKeyIsMissing()
    {
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var web = await database.CreateWebUserAsync();
        var commands = new OperatorCommands(
            database.AdminConnectionString,
            new PasswordHasher<WorkbenchUser>(),
            TimeProvider.System);
        var tenantId = await commands.BootstrapAsync(
            "First Tenant",
            "first-admin@example.com",
            "Correct Horse Battery Staple 1!",
            CancellationToken.None);
        await using (var admin = new SqlConnection(database.AdminConnectionString))
        {
            await admin.OpenAsync();
            await new SqlCommand("DELETE FROM [Security].[TenantContextKeys]", admin)
                .ExecuteNonQueryAsync();
        }

        await using var connection = new SqlConnection(web);
        await connection.OpenAsync();
        foreach (var procedure in new[]
        {
            "[Identity].[ResolveCredential]",
            "[Identity].[ResolveRecoveryTarget]",
        })
        {
            await using var lookup = new SqlCommand(procedure, connection)
            {
                CommandType = CommandType.StoredProcedure,
            };
            lookup.Parameters.AddWithValue("@NormalizedEmail", "FIRST-ADMIN@EXAMPLE.COM");
            lookup.Parameters.AddWithValue("@Nonce", new byte[32]);
            lookup.Parameters.AddWithValue("@Proof", new byte[32]);
            await Assert.ThrowsAsync<SqlException>(() => lookup.ExecuteNonQueryAsync());
        }

        await using var invitation = new SqlCommand("""
            EXEC sys.sp_set_session_context @key=N'TenantId', @value=@tenantId;
            EXEC sys.sp_set_session_context @key=N'TenantNonce', @value=@nonce;
            EXEC sys.sp_set_session_context @key=N'TenantProof', @value=@proof;
            EXEC [Identity].[CreateInvitation]
                @TenantId=@tenantId,
                @UserId=@userId,
                @OperationId=@operationId,
                @Email=N'invited@example.com',
                @NormalizedEmail=N'INVITED@EXAMPLE.COM',
                @TokenHash=@tokenHash,
                @Now='2026-09-04T00:00:00+00:00',
                @Expires='2026-09-05T00:00:00+00:00';
            """, connection);
        invitation.Parameters.AddWithValue("@tenantId", tenantId);
        invitation.Parameters.AddWithValue("@nonce", new byte[32]);
        invitation.Parameters.AddWithValue("@proof", new byte[32]);
        invitation.Parameters.AddWithValue("@userId", Guid.CreateVersion7());
        invitation.Parameters.AddWithValue("@operationId", Guid.CreateVersion7());
        invitation.Parameters.AddWithValue("@tokenHash", new byte[32]);
        await Assert.ThrowsAsync<SqlException>(() => invitation.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task WebPrincipalCannotResolveIdentityLookupsWithoutApplicationProof()
    {
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var web = await database.CreateWebUserAsync();
        var commands = new OperatorCommands(
            database.AdminConnectionString,
            new PasswordHasher<WorkbenchUser>(),
            TimeProvider.System);
        await commands.BootstrapAsync(
            "First Tenant",
            "first-admin@example.com",
            "Correct Horse Battery Staple 1!",
            CancellationToken.None);

        await using var connection = new SqlConnection(web);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "EXEC [Identity].[ResolveCredential] @NormalizedEmail=N'FIRST-ADMIN@EXAMPLE.COM'",
            connection);

        await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());

        await using var recoveryCommand = new SqlCommand(
            "EXEC [Identity].[ResolveRecoveryTarget] @NormalizedEmail=N'FIRST-ADMIN@EXAMPLE.COM'",
            connection);
        await Assert.ThrowsAsync<SqlException>(() => recoveryCommand.ExecuteNonQueryAsync());

        foreach (var procedure in new[]
        {
            "[Identity].[ResolveCredential]",
            "[Identity].[ResolveRecoveryTarget]",
        })
        {
            await using var forgedLookup = new SqlCommand(procedure, connection)
            {
                CommandType = CommandType.StoredProcedure,
            };
            forgedLookup.Parameters.AddWithValue("@NormalizedEmail", "FIRST-ADMIN@EXAMPLE.COM");
            forgedLookup.Parameters.AddWithValue("@Nonce", new byte[32]);
            forgedLookup.Parameters.AddWithValue("@Proof", new byte[32]);
            await Assert.ThrowsAsync<SqlException>(() => forgedLookup.ExecuteNonQueryAsync());
        }
    }

    [Fact]
    public async Task WebPrincipalCannotCreateInvitationWithUnprovenTenantContext()
    {
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var web = await database.CreateWebUserAsync();
        var commands = new OperatorCommands(
            database.AdminConnectionString,
            new PasswordHasher<WorkbenchUser>(),
            TimeProvider.System);
        await commands.BootstrapAsync(
            "First Tenant",
            "first-admin@example.com",
            "Correct Horse Battery Staple 1!",
            CancellationToken.None);
        var foreignTenant = await commands.CreateAdditionalTenantAsync(
            "Foreign Tenant",
            "foreign-admin@example.com",
            "Correct Horse Battery Staple 2@",
            CancellationToken.None);

        await using var connection = new SqlConnection(web);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            EXEC sys.sp_set_session_context @key=N'TenantId', @value=@tenantId;
            EXEC [Identity].[CreateInvitation]
                @TenantId=@tenantId,
                @UserId=@userId,
                @OperationId=@operationId,
                @Email=N'foreign-invite@example.com',
                @NormalizedEmail=N'FOREIGN-INVITE@EXAMPLE.COM',
                @TokenHash=@tokenHash,
                @Now='2026-09-04T00:00:00+00:00',
                @Expires='2026-09-05T00:00:00+00:00';
            """, connection);
        command.Parameters.AddWithValue("@tenantId", foreignTenant);
        command.Parameters.AddWithValue("@userId", Guid.CreateVersion7());
        command.Parameters.AddWithValue("@operationId", Guid.CreateVersion7());
        command.Parameters.Add(new SqlParameter("@tokenHash", SqlDbType.Binary, 32)
        {
            Value = new byte[32],
        });

        await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task DatabaseRolesEnforceWebOperatorAndMigratorBoundaries()
    {
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var web = await database.CreateWebUserAsync();
        var operatorConnection = await database.CreateRoleUserAsync("workbench_operator");
        var migrator = await database.CreateRoleUserAsync("workbench_migrator");

        await ExecuteAsync(web, "SELECT 1");
        // GIVEN freshly migrated production roles, supplier read projections are narrow web-only authority.
        foreach (var projection in new[] { "SupplierItemControl", "SupplierPaymentControl", "SupplierReportBillIdentity" })
        {
            await ExecuteAsync(web, $"SELECT * FROM Purchasing.{projection}(NEWID(),NEWID())");
        }
        var worker = await database.CreateRoleUserAsync("workbench_worker");
        // WHEN either runtime asks to mutate financial history or derive source authority, SQL denies it.
        foreach (var runtime in new[] { web, worker })
        {
            foreach (var mutation in new[] { "RecordSupplierPayment", "ApplySupplierFunds", "ReverseSupplierApplication", "CorrectSupplierPayment", "DeriveRecognitionOpenItems" })
                await AssertDeniedAsync(runtime, $"EXEC Purchasing.{mutation}", 229);
            await AssertDeniedAsync(runtime, "DELETE Purchasing.SupplierControlAttributions WHERE 1=0", 229);
            await AssertDeniedAsync(runtime, "UPDATE Purchasing.SupplierFinancialGroups SET SourceId=NEWID() WHERE 1=0", 229);
        }
        foreach (var projection in new[] { "SupplierItemControl", "SupplierPaymentControl", "SupplierReportBillIdentity" })
            await AssertDeniedAsync(worker, $"SELECT * FROM Purchasing.{projection}(NEWID(),NEWID())", 229);
        await AssertDeniedAsync(
            web,
            "ALTER SECURITY POLICY [Security].[TenantIsolationPolicy] WITH (STATE = OFF)");
        await AssertDeniedAsync(web, "DELETE FROM [dbo].[__EFMigrationsHistory]");
        await AssertDeniedAsync(web, "SELECT * FROM [Security].[TenantContextKeys]");
        await AssertDeniedAsync(web, "SELECT * FROM [Security].[SensitiveRequestLimits]");
        await AssertDeniedAsync(web, "UPDATE [Security].[WorkbenchRestorePending] SET [IsPending] = 0");
        await AssertDeniedAsync(operatorConnection, "SELECT TOP (1) * FROM [Identity].[Users]");
        await AssertDeniedAsync(operatorConnection, "CREATE TABLE [dbo].[OperatorMustNotCreate] ([Id] int)");
        await AssertDeniedAsync(
            operatorConnection,
            """
            EXEC [Administration].[CreateDevelopmentRecovery]
                @OperationId = '01991a86-2e00-7000-8000-000000000001',
                @NormalizedEmail = N'operator-admin@example.com',
                @TokenHash = 0x0000000000000000000000000000000000000000000000000000000000000000,
                @Now = '2026-09-02T00:00:00+00:00',
                @ExpiresAtUtc = '2026-09-02T00:30:00+00:00';
            """);

        var commands = new OperatorCommands(
            operatorConnection,
            new PasswordHasher<WorkbenchUser>(),
            TimeProvider.System);
        await commands.BootstrapAsync(
            "Operator Tenant",
            "operator-admin@example.com",
            "Correct Horse Battery Staple 5%",
            CancellationToken.None);

        await ExecuteAsync(migrator, "CREATE TABLE [dbo].[MigrationProbe] ([Id] int NOT NULL)");
    }

    private static async Task AssertDeniedAsync(string connectionString, string sql, int? expectedNumber = null)
    {
        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(connectionString, sql));
        if (expectedNumber.HasValue) Assert.Equal(expectedNumber.Value, error.Number);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
