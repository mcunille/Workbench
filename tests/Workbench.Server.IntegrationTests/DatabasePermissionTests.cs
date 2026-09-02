// Copyright (c) 2026 The White Stag Collection.

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
