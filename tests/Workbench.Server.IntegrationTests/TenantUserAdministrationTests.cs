// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    public async Task TenantAdministratorCannotDisableTheCurrentAndLastAdministrator()
    {
        var response = await SendDeleteWithAntiforgeryAsync(AuthTestApplication.AdminUserId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task InvitedAccountCannotBeReactivatedWithoutConsumingItsInvitation()
    {
        const string email = "pending-invite@example.com";
        Assert.Equal(HttpStatusCode.Accepted, (await RecoveryTests.PostWithAntiforgeryAsync(
            _admin,
            "/api/tenant/users/invitations",
            new { email })).StatusCode);
        var users = await _admin.GetFromJsonAsync<Workbench.Server.Administration.TenantUserResponse[]>(
            "/api/tenant/users");
        var invited = Assert.Single(users!, user => user.Email == email);

        var response = await RecoveryTests.PostWithAntiforgeryAsync(
            _admin,
            $"/api/tenant/users/{invited.Id}/reactivate",
            new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TenantAdministratorCanCancelPendingInvitationWithoutAllowingReactivation()
    {
        const string email = "cancelled-invite@example.com";
        Assert.Equal(HttpStatusCode.Accepted, (await RecoveryTests.PostWithAntiforgeryAsync(
            _admin, "/api/tenant/users/invitations", new { email })).StatusCode);
        var token = Assert.Single(_application.Factory.Services
            .GetRequiredService<Workbench.Server.Identity.DevelopmentIdentityMessageDelivery>()
            .Messages).Token;
        var users = await _admin.GetFromJsonAsync<Workbench.Server.Administration.TenantUserResponse[]>(
            "/api/tenant/users");
        var invited = Assert.Single(users!, user => user.Email == email);

        Assert.Equal(HttpStatusCode.NoContent, (await SendDeleteWithAntiforgeryAsync(invited.Id)).StatusCode);
        users = await _admin.GetFromJsonAsync<Workbench.Server.Administration.TenantUserResponse[]>(
            "/api/tenant/users");
        Assert.Equal(Workbench.Server.Identity.AccountState.Disabled,
            Assert.Single(users!, user => user.Id == invited.Id).State);
        Assert.Equal(HttpStatusCode.BadRequest, (await RecoveryTests.PostWithAntiforgeryAsync(
            _admin, $"/api/tenant/users/{invited.Id}/reactivate", new { })).StatusCode);
        using var anonymous = _application.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await RecoveryTests.PostWithAntiforgeryAsync(
            anonymous, "/api/auth/invitations/consume",
            new { token, newPassword = "Cancelled Invitation 8!" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RecoveryTests.PostWithAntiforgeryAsync(
            anonymous, "/api/auth/login", new { email, password = "Cancelled Invitation 8!" })).StatusCode);
    }

    [Fact]
    public async Task TenantAdministratorCanRevokeOwnTenantUserSessions()
    {
        using var member = _application.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await RecoveryTests.PostWithAntiforgeryAsync(
            member,
            "/api/auth/login",
            new { email = "member@example.com", password = AuthTestApplication.AdminPassword }))
            .StatusCode);

        var response = await SendDeleteWithAntiforgeryAsync(
            $"/api/tenant/users/{AuthTestApplication.MemberUserId}/sessions");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task AdministratorRecoveryReportsUnavailableWhenPublicOperationsAreDisabled()
    {
        await using var unavailable = await AuthTestApplication.CreateAsync(
            sqlServer,
            disablePublicOperations: true);
        using var admin = unavailable.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await RecoveryTests.PostWithAntiforgeryAsync(
            admin,
            "/api/auth/login",
            new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.AdminPassword }))
            .StatusCode);

        var response = await RecoveryTests.PostWithAntiforgeryAsync(
            admin,
            $"/api/tenant/users/{AuthTestApplication.AdminUserId}/recovery",
            new { });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
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
        using var identity = JsonDocument.Parse(
            await (await anonymous.GetAsync("/api/auth/me")).Content.ReadAsStringAsync());
        Assert.Contains(
            "TenantAccess",
            identity.RootElement.GetProperty("permissions")
                .EnumerateArray()
                .Select(permission => permission.GetString()));
    }

    private async Task<HttpResponseMessage> SendDeleteWithAntiforgeryAsync(Guid userId)
        => await SendDeleteWithAntiforgeryAsync($"/api/tenant/users/{userId}");

    private async Task<HttpResponseMessage> SendDeleteWithAntiforgeryAsync(string path)
    {
        var tokens = await _admin.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/auth/antiforgery");
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        request.Headers.Add("X-CSRF-TOKEN", tokens.GetProperty("requestToken").GetString());
        return await _admin.SendAsync(request);
    }
}
