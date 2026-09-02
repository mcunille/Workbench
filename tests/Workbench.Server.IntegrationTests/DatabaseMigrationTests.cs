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
}
