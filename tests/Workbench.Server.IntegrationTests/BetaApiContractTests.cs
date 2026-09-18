// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class BetaApiContractTests
{
    [Fact]
    public async Task OpenApiExposesOnlyTheBetaBusinessContract()
    {
        // GIVEN the runtime-generated document, including all endpoint registrations.
        await using var application = new WebApplicationFactory<Program>();
        using var client = application.CreateClient();
        var provider = application.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("beta");
        // WHEN consumers generate a client THEN no retired route or receipt DTO becomes public.
        var document = await provider.GetOpenApiDocumentAsync(default);
        Assert.Equal("beta", document.Info.Version);
        Assert.Contains("/api/beta/purchase-order-drafts", document.Paths.Keys);
        Assert.All(document.Paths.Keys, path => Assert.StartsWith("/api/beta/", path));
        Assert.DoesNotContain(document.Components!.Schemas!.Keys, name => name.StartsWith("Receipt", StringComparison.Ordinal) ||
            name.EndsWith("V2", StringComparison.Ordinal) || name.EndsWith("V3", StringComparison.Ordinal) || name.EndsWith("V4", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("obsolete-client")]
    public async Task BootstrapDoesNotNegotiateARevision(string? obsoleteHeader)
    {
        // GIVEN a browser using the single evolving beta, optionally retaining an obsolete header.
        await using var application = new WebApplicationFactory<Program>();
        using var client = application.CreateClient();
        if (obsoleteHeader is not null) client.DefaultRequestHeaders.Add("X-Workbench-Api-Revision", obsoleteHeader);
        // WHEN it discovers application identity THEN no revision is negotiated or advertised.
        var response = await client.GetAsync("/api/beta/system");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Workbench", body.GetProperty("name").GetString());
        Assert.False(body.TryGetProperty("apiRevision", out _));
        Assert.False(response.Headers.Contains("X-Workbench-Api-Revision"));
    }

    [Fact]
    public async Task HeaderlessWritesStillRequireAuthentication()
    {
        // GIVEN an anonymous browser with no contract revision header.
        await using var application = new WebApplicationFactory<Program>();
        using var client = application.CreateClient();
        // WHEN it attempts a protected write THEN normal authentication rejects it.
        var response = await client.PostAsJsonAsync("/api/beta/items", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HeaderlessAuthenticationWritesStillRequireAntiforgery()
    {
        // GIVEN an anonymous browser without an antiforgery token or revision header.
        await using var application = new WebApplicationFactory<Program>();
        using var client = application.CreateClient();
        // WHEN it submits login THEN ordinary antiforgery protection still applies.
        var response = await client.PostAsJsonAsync("/api/beta/auth/login", new { email = "example@example.test", password = "example" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
    [Theory]
    [InlineData("/api/system")]
    [InlineData("/api/v2/items")]
    public async Task RetiredPathsReturnReloadGuidance(string path)
    {
        // GIVEN a retired API path with no business compatibility adapter.
        await using var application = new WebApplicationFactory<Program>();
        using var client = application.CreateClient();
        // WHEN a stale browser reads it THEN it receives a machine-readable rejection.
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("api_contract_unsupported", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
}
