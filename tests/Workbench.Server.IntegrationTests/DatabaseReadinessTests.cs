// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
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
        Assert.False(reader.GetBoolean(6), "No restore may be pending.");
        Assert.True(reader.GetBoolean(7), "The tenant proof key must be protected.");
        Assert.True(reader.GetBoolean(8), "The shared sensitive-request limiter must be available.");

        var response = await _client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PriorReleaseSchemaIsUnreadyUntilDeploymentMigrationIsApplied()
    {
        // GIVEN the previous release schema remains valid but lacks deployment telemetry.
        await using var prior = await AuthTestApplication.CreateAsync(sqlServer, priorMigration: "AddBlobAndOperationalProviders");
        using var client = prior.CreateClient();
        // WHEN the current application probes that older schema.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        // THEN applying the required deployment migration makes this release ready.
        await DatabaseMigrator.MigrateAsync(prior.AdminConnectionString, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Theory]
    [InlineData("DROP PROCEDURE [Operations].[ReadWorkQueueStatus]")]
    [InlineData("REVOKE EXECUTE ON [Operations].[ReadWorkQueueStatus] FROM [workbench_worker]")]
    public async Task MissingQueueTelemetryAuthorityMakesReadinessUnhealthy(string breakTelemetry)
    {
        // GIVEN the required worker telemetry procedure or its execution authority is missing.
        await using var connection = new SqlConnection(_application.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(breakTelemetry, connection);
        await command.ExecuteNonQueryAsync();
        // WHEN readiness examines the deployed database.
        var response = await _client.GetAsync("/health/ready");
        // THEN this release does not advertise readiness with incomplete worker telemetry.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
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

    [Fact]
    public async Task MismatchedWorkloadTenantProofMakesReadinessUnhealthy()
    {
        await using var mismatchedFactory = _application.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting(
                "TenantContext:ProofKey",
                Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
        using var mismatchedClient = mismatchedFactory.CreateClient();

        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (await mismatchedClient.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task PendingRestoreMakesReadinessUnhealthyUntilSanitized()
    {
        await using (var connection = new SqlConnection(_application.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "EXEC [Administration].[MarkRestorePending]",
                connection);
            await command.ExecuteNonQueryAsync();
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _client.GetAsync("/health/ready")).StatusCode);

        await using (var connection = new SqlConnection(_application.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "EXEC [Administration].[SanitizeRestore] @Now=@now, @CorrelationId=N'readiness-test'",
                connection);
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow);
            await command.ExecuteNonQueryAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/ready")).StatusCode);
    }
}
