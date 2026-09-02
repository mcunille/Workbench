// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class AuthEndpointTests(SqlServerFixture sqlServer) : IAsyncLifetime
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

    [Theory]
    [InlineData("missing@example.com", "wrong")]
    [InlineData(AuthTestApplication.DisabledEmail, AuthTestApplication.AdminPassword)]
    public async Task LoginFailuresHaveSameContract(string email, string password)
    {
        var response = await PostWithAntiforgeryAsync(
            "/api/auth/login",
            new { email, password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Authentication failed.", problem?.Title);
    }

    [Fact]
    public async Task LoginAndMeExposeAuthoritativeIdentityWithoutTenantIdentifier()
    {
        var login = await LoginAsync();

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), value =>
            value.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase) &&
            value.Contains("SameSite=Lax", StringComparison.OrdinalIgnoreCase));

        var me = await _client.GetAsync("/api/auth/me");
        var json = await me.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(AuthTestApplication.AdminUserId, document.RootElement.GetProperty("userId").GetGuid());
        Assert.Equal(AuthTestApplication.AdminEmail, document.RootElement.GetProperty("email").GetString());
        Assert.Equal("Tenant A", document.RootElement.GetProperty("tenantName").GetString());
        Assert.Contains("TenantUsersManage", document.RootElement.GetProperty("permissions")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.False(document.RootElement.TryGetProperty("tenantId", out _));
    }

    [Fact]
    public async Task MeRejectsAnonymousRequestWithoutRedirect()
    {
        var response = await _client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task DurableRevocationImmediatelyRejectsCookie()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await LoginAsync()).StatusCode);
        await using (var connection = new SqlConnection(_application.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(
                "UPDATE [Identity].[Sessions] SET [RevokedAtUtc] = SYSUTCDATETIME(), [RevocationReason] = N'Test'",
                connection);
            await command.ExecuteNonQueryAsync();
        }

        var response = await _client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SessionsExposeSafeMetadataAndCurrentSessionCanBeRevoked()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await LoginAsync()).StatusCode);
        var sessions = await _client.GetFromJsonAsync<JsonElement>("/api/auth/sessions");
        var current = Assert.Single(sessions.EnumerateArray().ToArray());

        Assert.True(current.GetProperty("isCurrent").GetBoolean());
        Assert.False(current.TryGetProperty("token", out _));
        Assert.False(current.TryGetProperty("tokenHash", out _));

        var revoke = await SendWithAntiforgeryAsync(
            HttpMethod.Delete,
            $"/api/auth/sessions/{current.GetProperty("id").GetGuid()}");

        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task UserCannotRevokeAnotherUsersSessionInSameTenant()
    {
        var otherSessionId = Guid.NewGuid();
        await using (var connection = new SqlConnection(_application.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand("""
                INSERT INTO [Identity].[Sessions]
                    ([Id], [TenantId], [UserId], [TokenHash], [SecurityVersion], [CreatedAtUtc],
                     [LastSeenAtUtc], [IdleExpiresAtUtc], [AbsoluteExpiresAtUtc])
                VALUES
                    (@id, @tenantId, @userId, HASHBYTES('SHA2_256', CONVERT(varbinary(max), NEWID())),
                     1, SYSUTCDATETIME(), SYSUTCDATETIME(), DATEADD(minute, 30, SYSUTCDATETIME()),
                     DATEADD(hour, 12, SYSUTCDATETIME()));
                """, connection);
            command.Parameters.AddWithValue("@id", otherSessionId);
            command.Parameters.AddWithValue("@tenantId", AuthTestApplication.TenantId);
            command.Parameters.AddWithValue("@userId", AuthTestApplication.DisabledUserId);
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(HttpStatusCode.NoContent, (await LoginAsync()).StatusCode);

        var response = await SendWithAntiforgeryAsync(
            HttpMethod.Delete,
            $"/api/auth/sessions/{otherSessionId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var verifyConnection = new SqlConnection(_application.AdminConnectionString);
        await verifyConnection.OpenAsync();
        await using var verify = new SqlCommand(
            "SELECT [RevokedAtUtc] FROM [Identity].[Sessions] WHERE [Id] = @id",
            verifyConnection);
        verify.Parameters.AddWithValue("@id", otherSessionId);
        Assert.Equal(DBNull.Value, await verify.ExecuteScalarAsync());
    }

    [Fact]
    public async Task PasswordChangeRejectsOldPasswordAndRevokesCurrentSession()
    {
        const string newPassword = "New Correct Horse Battery 2@";
        Assert.Equal(HttpStatusCode.NoContent, (await LoginAsync()).StatusCode);

        var changed = await PostWithAntiforgeryAsync(
            "/api/auth/change-password",
            new
            {
                currentPassword = AuthTestApplication.AdminPassword,
                newPassword,
            });

        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWithAntiforgeryAsync(
            "/api/auth/login",
            new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.AdminPassword }))
            .StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostWithAntiforgeryAsync(
            "/api/auth/login",
            new { email = AuthTestApplication.AdminEmail, password = newPassword }))
            .StatusCode);
    }

    private Task<HttpResponseMessage> LoginAsync() => PostWithAntiforgeryAsync(
        "/api/auth/login",
        new
        {
            email = AuthTestApplication.AdminEmail,
            password = AuthTestApplication.AdminPassword,
        });

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

    private async Task<HttpResponseMessage> SendWithAntiforgeryAsync(HttpMethod method, string path)
    {
        var tokenResponse = await _client.GetFromJsonAsync<JsonElement>("/api/auth/antiforgery");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-CSRF-TOKEN", tokenResponse.GetProperty("requestToken").GetString());
        return await _client.SendAsync(request);
    }
}
