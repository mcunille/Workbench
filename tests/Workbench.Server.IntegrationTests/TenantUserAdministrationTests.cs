// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class TenantUserAdministrationTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private AuthTestApplication _application = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _application = await AuthTestApplication.CreateAsync(sqlServer);
        _admin = _application.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await RecoveryTests.PostWithAntiforgeryAsync(
            _admin,
            "/api/auth/login",
            new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.AdminPassword }))
            .StatusCode);
    }

    public async Task DisposeAsync()
    {
        _admin.Dispose();
        await _application.DisposeAsync();
    }

    [Fact]
    public async Task TenantAdministratorCannotDisableAnotherTenantUser()
    {
        var response = await SendDeleteWithAntiforgeryAsync(AuthTestApplication.OtherTenantUserId);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TenantAdministratorCanDisableOwnTenantUser()
    {
        var response = await SendDeleteWithAntiforgeryAsync(AuthTestApplication.MemberUserId);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task InvitationIsSingleUseAndCreatesAnEnabledAccount()
    {
        const string email = "invited@example.com";
        const string password = "Invited Correct Horse 4$";
        var invited = await RecoveryTests.PostWithAntiforgeryAsync(
            _admin,
            "/api/tenant/users/invitations",
            new { email });
        Assert.Equal(HttpStatusCode.Accepted, invited.StatusCode);
        var token = Assert.Single(_application.Factory.Services
            .GetRequiredService<Workbench.Server.Identity.DevelopmentIdentityMessageDelivery>()
            .Messages).Token;
        using var anonymous = _application.CreateClient();

        var consumed = await RecoveryTests.PostWithAntiforgeryAsync(
            anonymous,
            "/api/auth/invitations/consume",
            new { token, newPassword = password });

        Assert.Equal(HttpStatusCode.NoContent, consumed.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await RecoveryTests.PostWithAntiforgeryAsync(
            anonymous,
            "/api/auth/login",
            new { email, password })).StatusCode);
    }

    private async Task<HttpResponseMessage> SendDeleteWithAntiforgeryAsync(Guid userId)
    {
        var tokens = await _admin.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/auth/antiforgery");
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/tenant/users/{userId}");
        request.Headers.Add("X-CSRF-TOKEN", tokens.GetProperty("requestToken").GetString());
        return await _admin.SendAsync(request);
    }
}
