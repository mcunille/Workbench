// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Server.Application;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class BrowserSecurityHeadersTests
{
    [Theory]
    [InlineData("/", HttpStatusCode.OK, "text/html")]
    [InlineData("/index.html", HttpStatusCode.OK, "text/html")]
    [InlineData("/inventory/items", HttpStatusCode.OK, "text/html")]
    [InlineData("/api/system", HttpStatusCode.OK, "application/json")]
    [InlineData("/api/not-a-route", HttpStatusCode.NotFound, "application/problem+json")]
    [InlineData("/api/auth/me", HttpStatusCode.Unauthorized, null)]
    [InlineData("/api/items/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/photo/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/thumbnail", HttpStatusCode.Unauthorized, null)]
    public async Task ResponsesKeepTheirContractsAndDenyFraming(string path, HttpStatusCode status, string? contentType)
    {
        // GIVEN the real application pipeline and an HTTPS browser request.
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://workbench.example"), AllowAutoRedirect = false });
        // WHEN static files, SPA routes, API endpoints or authentication failures respond.
        using var response = await client.GetAsync(path);
        // THEN their status/content contracts survive and browser protections apply.
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        AssertBaseline(response);
        Assert.Equal("max-age=31536000", Assert.Single(response.Headers.GetValues("Strict-Transport-Security")));
    }

    [Fact]
    public async Task ExceptionResponseRetainsHeadersAfterResponseIsCleared()
    {
        // GIVEN an endpoint dependency that throws inside the exception handler boundary.
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IReleaseInformation>();
                services.AddSingleton<IReleaseInformation, ThrowingReleaseInformation>();
            }));
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://workbench.example") });
        // WHEN the handler replaces the response, THEN its Problem Details retain every protection.
        using var response = await client.GetAsync("/api/system");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        AssertBaseline(response);
        Assert.Equal("max-age=31536000", Assert.Single(response.Headers.GetValues("Strict-Transport-Security")));
    }

    [Theory]
    [InlineData("http://workbench.example")]
    [InlineData("http://localhost")]
    [InlineData("https://localhost")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://[::1]")]
    public async Task LocalAndHttpHealthRequestsStayUsableWithoutHsts(string origin)
    {
        // GIVEN local development or an internal HTTP health probe.
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri(origin), AllowAutoRedirect = false });
        // WHEN liveness is checked, THEN no redirect or persistent local HTTPS requirement is introduced.
        using var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
        Assert.Null(response.Headers.Location);
        AssertBaseline(response);
    }

    [Theory]
    [InlineData("10.42.0.2", true)]
    [InlineData("10.42.0.3", false)]
    public async Task HstsUsesOnlyTheTrustedForwardedScheme(string peer, bool expectedHsts)
    {
        // GIVEN the actual app pipeline with one trusted ingress peer.
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ReverseProxy:KnownProxies:0", "10.42.0.2"));
        // WHEN an HTTP request claims that the original browser connection used TLS.
        var context = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("workbench.example");
            context.Request.Path = "/health/live";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
        });
        // THEN only the explicitly trusted peer can cause an HSTS response.
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(expectedHsts, context.Response.Headers.ContainsKey("Strict-Transport-Security"));
    }

    private static void AssertBaseline(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        // The deliberately narrow policy leaves bundled scripts, CSS and blob photo previews unrestricted.
        Assert.Equal("frame-ancestors 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
    }

    private sealed class ThrowingReleaseInformation : IReleaseInformation
    {
        public string Version => throw new InvalidOperationException("Test dependency failure.");
    }
}
