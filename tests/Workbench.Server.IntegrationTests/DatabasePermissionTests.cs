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
        await AssertDeniedAsync(operatorConnection, "SELECT TOP (1) * FROM [Identity].[Users]");
        await AssertDeniedAsync(operatorConnection, "CREATE TABLE [dbo].[OperatorMustNotCreate] ([Id] int)");

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
