// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Server.Application;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class ErrorAndRoutingTests
{
    [Fact]
    public async Task ApiMissReturnsProblemDetailsInsteadOfSpaDocument()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/not-a-route");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("<html", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClientRouteReturnsSpaDocument()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/client/route");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<title>Workbench</title>", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnmatchedPostDoesNotReturnSpaDocument()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/client/route", content: null);
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("<html", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProductionExceptionReturnsGenericProblemDetails()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IReleaseInformation>();
                    services.AddSingleton<IReleaseInformation, ThrowingReleaseInformation>();
                });
            });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/system");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("An unexpected error occurred.", problem?.Title);
        Assert.Equal("https://www.rfc-editor.org/rfc/rfc9110#section-15.6.1", problem?.Type);
        object? traceId = null;
        Assert.True(problem is not null && problem.Extensions.TryGetValue("traceId", out traceId));
        Assert.False(string.IsNullOrWhiteSpace(traceId?.ToString()));
        Assert.DoesNotContain(ThrowingReleaseInformation.Sentinel, content, StringComparison.Ordinal);
    }

    private sealed class ThrowingReleaseInformation : IReleaseInformation
    {
        internal const string Sentinel = "sensitive-exception-sentinel";

        public string Version => throw new InvalidOperationException(Sentinel);
    }
}
