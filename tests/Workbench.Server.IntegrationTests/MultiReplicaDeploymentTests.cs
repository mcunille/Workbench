// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Server.Identity;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class MultiReplicaDeploymentTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task CookieAndAntiforgeryWorkAcrossHostsAndRevocationIsImmediatelyShared()
    {
        // GIVEN independent HTTP hosts using the same SQL sessions and durable protection key ring.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        await using var firstHost = CreateReplica(application);
        await using var secondHost = CreateReplica(application);
        using var first = CreateClient(firstHost);
        using var second = CreateClient(secondHost);
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        using var login = await SendWithAntiforgeryAsync(first, first, cookies, HttpMethod.Post,
            "/api/auth/login", new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.AdminPassword });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.NotSame(firstHost.Services.GetRequiredService<IDataProtectionProvider>(),
            secondHost.Services.GetRequiredService<IDataProtectionProvider>());

        // WHEN the cookie issued by the first host is sent to the second host.
        using var identity = await SendAsync(second, cookies, HttpMethod.Get, "/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, identity.StatusCode);
        var me = await identity.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(AuthTestApplication.AdminUserId, me.GetProperty("userId").GetGuid());
        using var sessionsResponse = await SendAsync(second, cookies, HttpMethod.Get, "/api/auth/sessions");
        Assert.Equal(HttpStatusCode.OK, sessionsResponse.StatusCode);
        var sessions = await sessionsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var session = Assert.Single(sessions.EnumerateArray().ToArray());

        var originalSessionCookies = new Dictionary<string, string>(cookies, StringComparer.Ordinal);
        // AND a token issued by the first host authorizes revocation on the second host.
        using var revoked = await SendWithAntiforgeryAsync(first, second, cookies, HttpMethod.Delete,
            $"/api/auth/sessions/{session.GetProperty("id").GetGuid()}");
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        // THEN both hosts immediately reject the revoked durable session.
        using var firstRejected = await SendAsync(first, new(originalSessionCookies), HttpMethod.Get, "/api/auth/me");
        using var secondRejected = await SendAsync(second, new(originalSessionCookies), HttpMethod.Get, "/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, firstRejected.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, secondRejected.StatusCode);
        await using var connection = new SqlConnection(application.AdminConnectionString);
        await connection.OpenAsync();
        await using var keys = new SqlCommand("SELECT COUNT(*) FROM [Identity].[DataProtectionKeys]", connection);
        Assert.True(Convert.ToInt32(await keys.ExecuteScalarAsync()) > 0);
    }

    [Fact]
    public async Task AlternatingHttpHostsShareSqlLoginBudgetAndIgnoreForgedForwardedAddresses()
    {
        // GIVEN two independent HTTP hosts with the production SQL limiter and a shared database.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        await using var firstHost = CreateReplica(application);
        await using var secondHost = CreateReplica(application);
        using var first = CreateClient(firstHost);
        using var second = CreateClient(secondHost);
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);

        // WHEN valid logins alternate across hosts while the untrusted client forges a new forwarded IP each time.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            cookies.Clear();
            var host = attempt % 2 == 0 ? first : second;
            using var allowed = await SendWithAntiforgeryAsync(host, host, cookies, HttpMethod.Post,
                "/api/auth/login", new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.AdminPassword },
                $"203.0.113.{attempt + 1}");
            Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        }

        // THEN the sixth valid login is rejected on both hosts because the SQL budget is shared.
        foreach (var host in new[] { second, first })
        {
            cookies.Clear();
            using var rejected = await SendWithAntiforgeryAsync(host, host, cookies, HttpMethod.Post,
                "/api/auth/login", new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.AdminPassword },
                "203.0.113.200");
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }
        // AND the spoofed addresses did not create additional network partitions.
        await using var connection = new SqlConnection(application.AdminConnectionString);
        await connection.OpenAsync();
        await using var partitions = new SqlCommand("SELECT COUNT(*) FROM [Security].[SensitiveRequestLimits]", connection);
        Assert.Equal(2, Convert.ToInt32(await partitions.ExecuteScalarAsync()));
    }

    private static WebApplicationFactory<Program> CreateReplica(AuthTestApplication application) =>
        application.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            // Keep Development's local HTTP transport, but use the actual production SQL limiter.
            services.RemoveAll<ISensitiveRequestRateLimiter>();
            services.AddSingleton<ISensitiveRequestRateLimiter>(provider =>
                new SqlSensitiveRequestRateLimiter(application.WebConnectionString, provider.GetRequiredService<TenantContextProof>()));
        }));

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) => factory.CreateClient(new()
    {
        AllowAutoRedirect = false,
        HandleCookies = false,
    });

    private static async Task<HttpResponseMessage> SendWithAntiforgeryAsync(HttpClient issuer, HttpClient receiver,
        Dictionary<string, string> cookies, HttpMethod method, string path, object? body = null, string? forwardedFor = null)
    {
        using var antiforgery = await SendAsync(issuer, cookies, HttpMethod.Get, "/api/auth/antiforgery");
        Assert.Equal(HttpStatusCode.OK, antiforgery.StatusCode);
        var token = await antiforgery.Content.ReadFromJsonAsync<JsonElement>();
        return await SendAsync(receiver, cookies, method, path, body, token.GetProperty("requestToken").GetString(), forwardedFor);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, Dictionary<string, string> cookies,
        HttpMethod method, string path, object? body = null, string? token = null, string? forwardedFor = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        if (cookies.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", cookies.Values));
        }
        if (token is not null)
        {
            request.Headers.Add("X-CSRF-TOKEN", token);
        }
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }
        var response = await client.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var value in setCookies)
            {
                var cookie = value.Split(';', 2)[0];
                cookies[cookie.Split('=', 2)[0]] = cookie;
            }
        }
        return response;
    }
}
