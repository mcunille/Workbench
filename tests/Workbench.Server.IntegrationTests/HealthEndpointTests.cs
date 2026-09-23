// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task LivenessDoesNotRequireDatabaseConfiguration()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new HealthPayload("Healthy"), await response.Content.ReadFromJsonAsync<HealthPayload>());
    }

    [Fact]
    public async Task ReadinessFailsWhenDatabaseConfigurationIsMissing()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(new HealthPayload("Unhealthy"), await response.Content.ReadFromJsonAsync<HealthPayload>());
    }
}

[Collection(SqlServerCollection.Name)]
public sealed class HealthEndpointSqlTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task FailingDependencyStopsReadinessWithoutStoppingLiveness()
    {
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        var dependencyReady = true;
        await using var factory = application.Factory
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddHealthChecks().AddCheck(
                    "controlled-dependency",
                    () => dependencyReady ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy(),
                    tags: ["ready"])));
        using var client = factory.CreateClient();

        // GIVEN a migrated database and a healthy ready-tagged dependency.
        // WHEN the application is probed before the dependency fails.
        var initialReadiness = await client.GetAsync("/health/ready");
        var initialLiveness = await client.GetAsync("/health/live");

        // THEN both endpoints report healthy.
        Assert.Equal(HttpStatusCode.OK, initialReadiness.StatusCode);
        Assert.Equal(new HealthPayload("Healthy"), await initialReadiness.Content.ReadFromJsonAsync<HealthPayload>());
        Assert.Equal(HttpStatusCode.OK, initialLiveness.StatusCode);
        Assert.Equal(new HealthPayload("Healthy"), await initialLiveness.Content.ReadFromJsonAsync<HealthPayload>());

        // WHEN only the ready-tagged dependency becomes unhealthy.
        dependencyReady = false;
        var readiness = await client.GetAsync("/health/ready");
        var liveness = await client.GetAsync("/health/live");

        // THEN readiness fails while liveness remains healthy.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        Assert.Equal(new HealthPayload("Unhealthy"), await readiness.Content.ReadFromJsonAsync<HealthPayload>());
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        Assert.Equal(new HealthPayload("Healthy"), await liveness.Content.ReadFromJsonAsync<HealthPayload>());
    }
}

file sealed record HealthPayload(string Status);
