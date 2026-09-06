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
        // GIVEN an empty database.
        await using var database = await sqlServer.CreateDatabaseAsync();

        // WHEN the complete release schema is applied.
        await DatabaseMigrator.MigrateAsync(
            database.AdminConnectionString,
            CancellationToken.None);

        await using var connection = new SqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT [MigrationId] FROM [dbo].[__EFMigrationsHistory] ORDER BY [MigrationId]",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var migrations = new List<string>();
        while (await reader.ReadAsync())
        {
            migrations.Add(reader.GetString(0));
        }

        // THEN this feature adds one migration after the established schema history.
        Assert.Collection(
            migrations,
            migration => Assert.EndsWith("_InitialSchema", migration, StringComparison.Ordinal),
            migration => Assert.EndsWith("_EstablishSecurityBoundaries", migration, StringComparison.Ordinal),
            migration => Assert.EndsWith("_AddBlobAndOperationalProviders", migration, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("InitialSchema")]
    [InlineData("EstablishSecurityBoundaries")]
    public async Task MigratorUpgradesASeededPriorSchemaWithoutLosingTenantData(string priorMigration)
    {
        // GIVEN tenant data in either the initial schema or the PR base schema.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateToAsync(
            database.AdminConnectionString,
            priorMigration,
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

        // WHEN the current release is applied.
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);

        // THEN existing tenant data survives the upgrade.
        Assert.Equal(1, await CountAsync(database.AdminConnectionString, "[Tenancy].[Tenants]"));
    }

    [Fact]
    public async Task RetainedMetadataCannotBeRolledBackDestructively()
    {
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);

        // GIVEN durable storage metadata, WHEN a destructive schema rollback is requested,
        // THEN it is refused and the current tables remain available for offline recovery.
        var error = await Assert.ThrowsAsync<SqlException>(() => DatabaseMigrator.MigrateToAsync(
            database.AdminConnectionString,
            "InitialSchema",
            CancellationToken.None));
        Assert.Equal(50020, error.Number);
        Assert.Equal(1, await ObjectCountAsync(database.AdminConnectionString, "Storage.Revisions"));
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
