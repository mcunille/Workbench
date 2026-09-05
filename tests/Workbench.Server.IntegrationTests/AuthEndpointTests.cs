// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Server.Identity;
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
        await using (var connection = new SqlConnection(_application.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand("""
                SELECT [TenantId], [ActorUserId], [TargetType], [TargetId], [Outcome],
                    [CorrelationId], [MetadataJson]
                FROM [Security].[TenantSecurityAuditEvents]
                WHERE [Action] = N'identity.password.changed';
                """, connection);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(AuthTestApplication.TenantId, reader.GetGuid(0));
            Assert.Equal(AuthTestApplication.AdminUserId, reader.GetGuid(1));
            Assert.Equal("User", reader.GetString(2));
            Assert.Equal(AuthTestApplication.AdminUserId, reader.GetGuid(3));
            Assert.Equal("Succeeded", reader.GetString(4));
            Assert.False(string.IsNullOrWhiteSpace(reader.GetString(5)));
            Assert.True(reader.IsDBNull(6));
            Assert.False(await reader.ReadAsync());
        }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionCreationRejectsCredentialsVerifiedBeforePasswordReplacement(bool recovery)
    {
        const string newPassword = "Replacement Correct Horse 7!";
        using var scope = _application.Factory.Services.CreateScope();
        var verifier = scope.ServiceProvider.GetRequiredService<IIdentityVerifier>();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
        var verified = await verifier.VerifyAsync(
            AuthTestApplication.AdminEmail, AuthTestApplication.AdminPassword, CancellationToken.None);
        Assert.NotNull(verified);

        if (recovery)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await PostWithAntiforgeryAsync(
                "/api/auth/recovery", new { email = AuthTestApplication.AdminEmail })).StatusCode);
            var token = Assert.Single(_application.Factory.Services
                .GetRequiredService<DevelopmentIdentityMessageDelivery>().Messages).Token;
            Assert.Equal(HttpStatusCode.NoContent, (await PostWithAntiforgeryAsync(
                "/api/auth/recovery/consume", new { token, newPassword })).StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.NoContent, (await LoginAsync()).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await PostWithAntiforgeryAsync(
                "/api/auth/change-password",
                new { currentPassword = AuthTestApplication.AdminPassword, newPassword })).StatusCode);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => sessions.CreateAsync(
            verified, DateTimeOffset.UtcNow, CancellationToken.None));
        await using var connection = new SqlConnection(_application.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM [Identity].[Sessions] WHERE [RevokedAtUtc] IS NULL", connection);
        Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));
        Assert.Equal(HttpStatusCode.NoContent, (await PostWithAntiforgeryAsync(
            "/api/auth/login", new { email = AuthTestApplication.AdminEmail, password = newPassword }))
            .StatusCode);
    }

    [Fact]
    public async Task PasswordChangeRollsBackWhenAuditCannotBePersisted()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await LoginAsync()).StatusCode);
        await using var connection = new SqlConnection(_application.AdminConnectionString);
        await connection.OpenAsync();
        await using (var rejectAudit = new SqlCommand("""
            ALTER TABLE [Security].[TenantSecurityAuditEvents]
            ADD CONSTRAINT [CK_Test_RejectPasswordChangeAudit]
                CHECK ([Action] <> N'identity.password.changed');
            """, connection))
        {
            await rejectAudit.ExecuteNonQueryAsync();
        }

        var response = await PostWithAntiforgeryAsync(
            "/api/auth/change-password",
            new { currentPassword = AuthTestApplication.AdminPassword, newPassword = "New Password 123!" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await LoginAsync()).StatusCode);
        await using var verify = new SqlCommand("""
            SELECT [SecurityVersion] FROM [Identity].[Users] WHERE [Id] = @id;
            SELECT COUNT(*) FROM [Security].[TenantSecurityAuditEvents];
            SELECT COUNT(*) FROM [Identity].[Sessions] WHERE [RevokedAtUtc] IS NOT NULL;
            """, connection);
        verify.Parameters.AddWithValue("@id", AuthTestApplication.AdminUserId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt32(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt32(0));
    }

    [Fact]
    public async Task PasswordChangeRejectsOversizedInputs()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await LoginAsync()).StatusCode);
        var oversized = new string('x', WorkbenchPasswordPolicy.MaximumLength + 1);

        var response = await PostWithAntiforgeryAsync(
            "/api/auth/change-password",
            new { currentPassword = oversized, newPassword = oversized });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PasswordChangeRejectsNullInputs(bool nullCurrentPassword)
    {
        Assert.Equal(HttpStatusCode.NoContent, (await LoginAsync()).StatusCode);
        var response = await PostWithAntiforgeryAsync(
            "/api/auth/change-password",
            nullCurrentPassword
                ? new ChangePasswordRequest(null!, "Valid New Password 2@")
                : new ChangePasswordRequest(AuthTestApplication.AdminPassword, null!));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LoginIsRateLimitedBeforeAnUnlimitedPasswordGuessCanSucceed()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var failure = await PostWithAntiforgeryAsync(
                "/api/auth/login",
                new { email = AuthTestApplication.AdminEmail, password = $"wrong-{attempt}" });
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        }

        var blockedValidPassword = await LoginAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, blockedValidPassword.StatusCode);
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
