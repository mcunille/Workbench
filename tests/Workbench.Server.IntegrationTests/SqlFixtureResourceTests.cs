// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;

namespace Workbench.Server.IntegrationTests;

[Xunit.Collection(SqlServerCollection.Name)]
public sealed class SqlFixtureResourceTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task SqlFixtureUsesABoundedBufferPool()
    {
        // GIVEN a running disposable SQL fixture and an independent test database.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await using var connection = new SqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        // WHEN SQL reports the effective memory ceiling used by its buffer pool.
        await using var command = new SqlCommand(
            "SELECT CONVERT(int, value_in_use) FROM sys.configurations WHERE name = 'max server memory (MB)'",
            connection);
        var memoryMb = (int)(await command.ExecuteScalarAsync())!;
        // THEN the effective buffer-pool ceiling matches this fixture's 1.5 GiB budget.
        Xunit.Assert.Equal(1536, memoryMb);
    }
}
