using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class HealthEndpointTests
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthyApplicationReturnsStableHealthContract(string path)
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new HealthPayload("Healthy"), await response.Content.ReadFromJsonAsync<HealthPayload>());
    }

    [Fact]
    public async Task FailingDependencyStopsReadinessWithoutStoppingLiveness()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                services.AddHealthChecks().AddCheck(
                    "failing-dependency",
                    () => HealthCheckResult.Unhealthy(),
                    tags: ["ready"])));
        using var client = factory.CreateClient();

        var readiness = await client.GetAsync("/health/ready");
        var liveness = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        Assert.Equal(new HealthPayload("Unhealthy"), await readiness.Content.ReadFromJsonAsync<HealthPayload>());
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        Assert.Equal(new HealthPayload("Healthy"), await liveness.Content.ReadFromJsonAsync<HealthPayload>());
    }

    private sealed record HealthPayload(string Status);
}
