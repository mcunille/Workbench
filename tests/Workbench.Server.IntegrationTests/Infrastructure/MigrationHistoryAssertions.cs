// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests.Infrastructure;

internal static class MigrationHistoryAssertions
{
    public static void AssertCurrent(IEnumerable<string> applied, params string[] retainedMigrations)
    {
        var expected = CurrentSchema.Migrations.Concat(retainedMigrations).Order(StringComparer.Ordinal);
        Assert.Equal(expected, applied, StringComparer.Ordinal);
    }

    public static async Task AssertCurrentAsync(string connectionString, params string[] retainedMigrations)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT [MigrationId] FROM [dbo].[__EFMigrationsHistory] ORDER BY [MigrationId]",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var applied = new List<string>();
        while (await reader.ReadAsync())
        {
            applied.Add(reader.GetString(0));
        }

        AssertCurrent(applied, retainedMigrations);
    }
}
