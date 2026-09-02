// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class AntiforgeryTests(SqlServerFixture sqlServer) : IAsyncLifetime
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
    public async Task LoginRequiresAntiforgery()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", ValidLogin());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LoginNeverAcceptsTenantIdentifier()
    {
        var response = await PostWithAntiforgeryAsync(
            "/api/auth/login",
            new
            {
                email = AuthTestApplication.AdminEmail,
                password = AuthTestApplication.AdminPassword,
                tenantId = Guid.NewGuid(),
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AntiforgeryCookieIsHttpOnlyAndStrictSameSite()
    {
        var response = await _client.GetAsync("/api/auth/antiforgery");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), value =>
            value.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase) &&
            value.Contains("SameSite=Strict", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LogoutWithoutAntiforgeryDoesNotRevokeSession()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await PostWithAntiforgeryAsync(
            "/api/auth/login",
            ValidLogin())).StatusCode);

        var logout = await _client.PostAsync("/api/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, logout.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/auth/me")).StatusCode);
    }

    private static object ValidLogin() => new
    {
        email = AuthTestApplication.AdminEmail,
        password = AuthTestApplication.AdminPassword,
    };

    private async Task<HttpResponseMessage> PostWithAntiforgeryAsync(string path, object body)
    {
        var tokenResponse = await _client.GetFromJsonAsync<JsonElement>("/api/auth/antiforgery");
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("X-CSRF-TOKEN", tokenResponse.GetProperty("requestToken").GetString());
        return await _client.SendAsync(request);
    }
}
