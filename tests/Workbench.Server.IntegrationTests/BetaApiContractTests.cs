// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class BetaApiContractTests
{
    [Fact]
    public async Task PurchaseDocumentUploadDescribesItsRequiredMultipartFields()
    {
        // GIVEN the runtime-generated contract used by generated API consumers.
        await using var application = new WebApplicationFactory<Program>();
        using var client = application.CreateClient();
        var provider = application.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("beta");
        var document = await provider.GetOpenApiDocumentAsync(default);
        // WHEN a consumer discovers the purchase document upload.
        var operation = document.Paths["/api/beta/purchase-orders/{id}/documents"].Operations![HttpMethod.Post];
        // THEN a required multipart body describes all four mandatory fields, including binary bytes.
        Assert.NotNull(operation.RequestBody);
        Assert.True(operation.RequestBody.Required);
        var schema = operation.RequestBody.Content!["multipart/form-data"].Schema!;
        Assert.Equal(new[] { "expectedOrderVersion", "file", "label", "requestId" }, schema.Required!.Order());
        Assert.Equal("binary", schema.Properties!["file"].Format);
        Assert.Equal("uuid", schema.Properties["requestId"].Format);
    }

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
        // GIVEN a SQL-free host and a well-formed login without a CSRF token or revision header.
        await using var application = CreateSqlFreeApplication();
        using var client = application.CreateClient();
        // WHEN it submits login THEN the response identifies antiforgery rejection, not another 400.
        var response = await client.PostAsJsonAsync("/api/beta/auth/login", new
        {
            email = "example@example.test",
            password = "Correct-Horse-Battery-Staple-47!",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Antiforgery validation failed.", problem.GetProperty("title").GetString());
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task AntiforgeryCookieIsHttpOnlyAndStrictSameSite()
    {
        // GIVEN a SQL-free development host with its normal antiforgery cookie configuration.
        await using var application = CreateSqlFreeApplication();
        using var client = application.CreateClient();

        // WHEN the browser requests a token THEN the emitted antiforgery cookie has both protections.
        var response = await client.GetAsync("/api/beta/auth/antiforgery");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith(".Workbench.Antiforgery=", StringComparison.Ordinal));
        Assert.Contains("HttpOnly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Strict", cookie, StringComparison.OrdinalIgnoreCase);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("requestToken").GetString()));
    }

    private static WebApplicationFactory<Program> CreateSqlFreeApplication() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            // Exclude inherited connection files and explicitly suppress the environment fallback.
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.Sources.Clear();
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Workbench"] = "",
                });
            });
            builder.ConfigureServices(services => services.AddDataProtection().UseEphemeralDataProtectionProvider());
        });

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
