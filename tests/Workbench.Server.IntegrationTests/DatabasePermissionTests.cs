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

    private static async Task AssertDeniedAsync(string connectionString, string sql)
    {
        await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(connectionString, sql));
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
