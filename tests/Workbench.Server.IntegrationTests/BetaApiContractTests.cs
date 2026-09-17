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
    [InlineData("beta-0")]
    [InlineData("beta-1")]
    [InlineData("beta-2,beta-2")]
    public async Task ExplicitlyIncompatibleReadsAlsoRequireReload(string revision)
    {
        // GIVEN a stale or ambiguous revision on a read request.
        await using var application = new WebApplicationFactory<Program>();
        using var client = application.CreateClient();
        client.DefaultRequestHeaders.Add("X-Workbench-Api-Revision", revision);
        // WHEN the request reaches even the bootstrap route THEN it is not silently treated as current.
        var response = await client.GetAsync("/api/beta/system");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task BootstrapIdentifiesTheBetaRevisionWithoutAClientRevision()
    {
        // GIVEN a browser bootstrapping without an API revision.
        await using var application = new WebApplicationFactory<Program>();
        using var client = application.CreateClient();
        // WHEN it discovers the application identity.
        var response = await client.GetAsync("/api/beta/system");
        // THEN it learns the explicit beta contract rather than a stable version.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("beta-2", body.GetProperty("apiRevision").GetString());
        Assert.Equal("beta-2", Assert.Single(response.Headers.GetValues("X-Workbench-Api-Revision")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("beta-0")]
    [InlineData("beta-1")]
    public async Task StaleWritesAreRejectedBeforeAuthenticationOrBinding(string? revision)
    {
        // GIVEN a stale browser, including a browser predating revision headers.
        await using var application = new WebApplicationFactory<Program>();
        using var client = application.CreateClient();
        if (revision is not null) client.DefaultRequestHeaders.Add("X-Workbench-Api-Revision", revision);
        // WHEN it submits a write with no valid current payload or credentials.
        var response = await client.PostAsJsonAsync("/api/beta/items", new { });
        // THEN the contract rejection takes precedence and no business handler runs.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("api_contract_unsupported", body.GetProperty("code").GetString());
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task CurrentRevisionStillRequiresAuthentication()
    {
        // GIVEN a current browser without a session.
        await using var application = new WebApplicationFactory<Program>();
        using var client = application.CreateClient();
        client.DefaultRequestHeaders.Add("X-Workbench-Api-Revision", "beta-2");
        // WHEN it attempts a protected write THEN authentication still applies.
        var response = await client.PostAsJsonAsync("/api/beta/items", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
