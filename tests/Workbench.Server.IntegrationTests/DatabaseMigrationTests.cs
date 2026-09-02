// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class DatabaseMigrationTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task MigratorCreatesCurrentSchemaOnEmptyDatabase()
    {
        await using var database = await sqlServer.CreateDatabaseAsync();

        await DatabaseMigrator.MigrateAsync(
            database.AdminConnectionString,
            CancellationToken.None);

        await using var connection = new SqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM [dbo].[__EFMigrationsHistory]",
            connection);

        var migrationCount = Convert.ToInt32(await command.ExecuteScalarAsync());

        Assert.True(migrationCount > 0);
    }

    [Fact]
    public async Task MigratorUpgradesASeededPriorSchemaWithoutLosingTenantData()
    {
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateToAsync(
            database.AdminConnectionString,
            "20260902042420_AddTenantIsolation",
            CancellationToken.None);
        await using (var connection = new SqlConnection(database.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = new SqlCommand("""
                INSERT INTO [Tenancy].[Tenants]
                    ([Id], [Name], [NormalizedName], [IsEnabled], [CreatedAtUtc])
                VALUES
                    ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', N'Upgrade Tenant',
                     N'UPGRADE TENANT', 1, SYSUTCDATETIME())
                """, connection);
            await seed.ExecuteNonQueryAsync();
        }

        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);

        Assert.Equal(1, await CountAsync(database.AdminConnectionString, "[Tenancy].[Tenants]"));
    }

    [Fact]
    public async Task LatestMigrationCanRollbackOneVersionAndReapply()
    {
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);

        await DatabaseMigrator.MigrateToAsync(
            database.AdminConnectionString,
            "20260902053620_AddDatabasePrincipals",
            CancellationToken.None);
        Assert.Equal(0, await ObjectCountAsync(database.AdminConnectionString, "Security.DatabaseSecurityState"));

        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        Assert.Equal(1, await ObjectCountAsync(database.AdminConnectionString, "Security.DatabaseSecurityState"));
    }

    private static async Task<int> CountAsync(string connectionString, string table)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"SELECT COUNT(*) FROM {table}", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> ObjectCountAsync(string connectionString, string name)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(@name)", connection);
        command.Parameters.AddWithValue("@name", name);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
