// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class DatabaseReadinessTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private AuthTestApplication _application = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _application = await AuthTestApplication.CreateAsync(sqlServer);
        _client = _application.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _application.DisposeAsync();
    }

    [Fact]
    public async Task MigratedSecureDatabaseIsReady()
    {
        await using var connection = new SqlConnection(_application.WebConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("[Security].[ReadDatabaseReadiness]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure,
        };
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0), "The migration must be compatible.");
        Assert.True(reader.GetBoolean(1), "The tenant policy must be enabled.");
        Assert.True(reader.GetBoolean(2), "Altering the tenant policy must be denied.");
        Assert.True(reader.GetBoolean(3), "The key table must be available.");
        Assert.Equal(reader.GetInt64(4), reader.GetInt64(5));

        var response = await _client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DisabledRlsMakesReadinessUnhealthyWithoutStoppingLiveness()
    {
        await using (var connection = new SqlConnection(_application.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "ALTER SECURITY POLICY [Security].[TenantIsolationPolicy] WITH (STATE = OFF)",
                connection);
            await command.ExecuteNonQueryAsync();
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/live")).StatusCode);
    }
}
