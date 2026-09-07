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

    [Theory]
    [InlineData("other@example.com")]
    [InlineData(" OTHER@EXAMPLE.COM ")]
    [InlineData("new-pending@example.com")]
    public async Task PendingInvitationDoesNotDiscloseOrReserveGlobalIdentity(string email)
    {
        // GIVEN an existing cross-tenant account or an unused email.
        var recipient = email.Trim();
        // WHEN the administrator requests an invitation.
        var response = await RecoveryTests.PostWithAntiforgeryAsync(
            _admin, "/api/tenant/users/invitations", new { email });
        // THEN the response and pending-user representation are identical.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var users = await _admin.GetFromJsonAsync<Workbench.Server.Administration.TenantUserResponse[]>(
            "/api/tenant/users");
        var pending = Assert.Single(users!, user => user.Email == recipient);
        Assert.Equal(Workbench.Server.Identity.AccountState.Invited, pending.State);
        // AND pending authority owns neither global login identifier.
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(_application.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand("""
            SELECT COUNT(*) FROM [Identity].[Users] u
            WHERE u.[Id] = @id AND u.[NormalizedUserName] IS NULL
                AND NOT EXISTS (SELECT 1 FROM [Identity].[LoginDirectory] d WHERE d.[UserId] = u.[Id]);
            """, connection);
        command.Parameters.AddWithValue("@id", pending.Id);
        Assert.Equal(1, (int)(await command.ExecuteScalarAsync())!);
    }
    [Fact]
    public async Task ConflictingInvitationCannotChangeAnExistingAccount()
    {
        // GIVEN another tenant owns the global identity.
        const string email = "other@example.com";
        // WHEN an invitation is requested and its recipient attempts consumption.
        Assert.Equal(HttpStatusCode.Accepted, (await RecoveryTests.PostWithAntiforgeryAsync(
            _admin, "/api/tenant/users/invitations", new { email })).StatusCode);
        var token = Assert.Single(_application.Factory.Services
            .GetRequiredService<Workbench.Server.Identity.DevelopmentIdentityMessageDelivery>().Messages).Token;
        using var anonymous = _application.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await RecoveryTests.PostWithAntiforgeryAsync(
            anonymous, "/api/auth/invitations/consume", new { token, newPassword = "Changed Password 9!" })).StatusCode);
        // THEN the original account still signs in with its original password.
        Assert.Equal(HttpStatusCode.NoContent, (await RecoveryTests.PostWithAntiforgeryAsync(
            anonymous, "/api/auth/login", new { email, password = AuthTestApplication.AdminPassword })).StatusCode);
        // AND the failed consumption leaves the pending account and token unchanged.
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(_application.AdminConnectionString);
        await connection.OpenAsync();
        await using var read = new Microsoft.Data.SqlClient.SqlCommand("""
            SELECT COUNT(*) FROM [Identity].[Users] u JOIN [Identity].[IdentityOperations] o
                ON o.[UserId] = u.[Id] AND o.[TenantId] = u.[TenantId]
            WHERE u.[Email] = N'other@example.com' AND u.[State] = 3
                AND u.[PasswordHash] IS NULL AND u.[NormalizedUserName] IS NULL
                AND u.[SecurityVersion] = 1 AND o.[ConsumedAtUtc] IS NULL;
            """, connection);
        Assert.Equal(1, (int)(await read.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task CompetingTenantInvitationsClaimIdentityOnlyOnce()
    {
        // GIVEN independent invitations in two tenants for the same mailbox.
        var secondTenant = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(_application.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var role = new Microsoft.Data.SqlClient.SqlCommand("""
                INSERT INTO [Identity].[Roles] ([Id], [TenantId], [Name], [NormalizedName])
                VALUES (NEWID(), @tenant, N'Tenant Member', N'TENANT MEMBER');
                """, connection);
            role.Parameters.AddWithValue("@tenant", secondTenant);
            await role.ExecuteNonQueryAsync();
        }
        using var scope = _application.Factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<Workbench.Server.Identity.IdentityOperationService>();
        const string email = "competing@example.com";
        Assert.True(await operations.RequestInvitationAsync(AuthTestApplication.TenantId, email, CancellationToken.None));
        Assert.True(await operations.RequestInvitationAsync(secondTenant, email.ToUpperInvariant(), CancellationToken.None));
        var messages = _application.Factory.Services
            .GetRequiredService<Workbench.Server.Identity.DevelopmentIdentityMessageDelivery>().Messages.ToArray();
        Assert.Equal(2, messages.Length);
        async Task<bool> ConsumeAsync(string token)
        {
            using var consumeScope = _application.Factory.Services.CreateScope();
            return await consumeScope.ServiceProvider.GetRequiredService<Workbench.Server.Identity.IdentityOperationService>()
                .ConsumeInvitationAsync(token, "Competing Password 9!", "test-race", CancellationToken.None);
        }
        // WHEN both valid tokens are consumed concurrently through separate SQL connections.
        var results = await Task.WhenAll(messages.Select(message => ConsumeAsync(message.Token)));
        // THEN exactly one global identity and one enabled account exist.
        Assert.Single(results, result => result);
        await using var check = new Microsoft.Data.SqlClient.SqlConnection(_application.AdminConnectionString);
        await check.OpenAsync();
        await using var read = new Microsoft.Data.SqlClient.SqlCommand("""
            SELECT COUNT(*) FROM [Identity].[LoginDirectory] d JOIN [Identity].[Users] u ON u.[Id] = d.[UserId]
            WHERE d.[NormalizedEmail] = N'COMPETING@EXAMPLE.COM' AND u.[State] = 1
                AND u.[NormalizedUserName] = d.[NormalizedEmail];
            """, check);
        Assert.Equal(1, (int)(await read.ExecuteScalarAsync())!);
        // AND neither invitation token can be consumed again.
        foreach (var message in messages)
        {
            Assert.False(await ConsumeAsync(message.Token));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledOrExpiredInvitationDoesNotPreventNewAcceptance(bool cancel)
    {
        // GIVEN an invitation that is cancelled or expired.
        const string email = "reinvite@example.com";
        Assert.Equal(HttpStatusCode.Accepted, (await RecoveryTests.PostWithAntiforgeryAsync(
            _admin, "/api/tenant/users/invitations", new { email })).StatusCode);
        var delivery = _application.Factory.Services
            .GetRequiredService<Workbench.Server.Identity.DevelopmentIdentityMessageDelivery>();
        var oldToken = Assert.Single(delivery.Messages).Token;
        if (cancel)
        {
            var users = await _admin.GetFromJsonAsync<Workbench.Server.Administration.TenantUserResponse[]>("/api/tenant/users");
            Assert.Equal(HttpStatusCode.NoContent, (await SendDeleteWithAntiforgeryAsync(
                Assert.Single(users!, user => user.Email == email).Id)).StatusCode);
        }
        else
        {
            await using var connection = new Microsoft.Data.SqlClient.SqlConnection(_application.AdminConnectionString);
            await connection.OpenAsync();
            await using var expire = new Microsoft.Data.SqlClient.SqlCommand("""
                UPDATE [Identity].[IdentityOperations] SET [ExpiresAtUtc] = DATEADD(hour, -1, SYSUTCDATETIME());
                """, connection);
            await expire.ExecuteNonQueryAsync();
        }
        // WHEN another invitation is issued for the same mailbox.
        Assert.Equal(HttpStatusCode.Accepted, (await RecoveryTests.PostWithAntiforgeryAsync(
            _admin, "/api/tenant/users/invitations", new { email })).StatusCode);
        var newToken = Assert.Single(delivery.Messages, message => message.Token != oldToken).Token;
        using var anonymous = _application.CreateClient();
        // THEN the old token fails and the new token activates the account.
        Assert.Equal(HttpStatusCode.BadRequest, (await RecoveryTests.PostWithAntiforgeryAsync(
            anonymous, "/api/auth/invitations/consume", new { token = oldToken, newPassword = "Reinvited Password 9!" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await RecoveryTests.PostWithAntiforgeryAsync(
            anonymous, "/api/auth/invitations/consume", new { token = newToken, newPassword = "Reinvited Password 9!" })).StatusCode);
    }
    [Theory]
    [InlineData(false, 50001)]
    [InlineData(true, 50004)]
    public async Task IdentityClaimRequiresTenantProofAndValidInvitation(bool useProof, int expectedError)
    {
        // GIVEN a pending invitation and the real web principal without its token.
        Assert.Equal(HttpStatusCode.Accepted, (await RecoveryTests.PostWithAntiforgeryAsync(
            _admin, "/api/tenant/users/invitations", new { email = "proof@example.com" })).StatusCode);
        var users = await _admin.GetFromJsonAsync<Workbench.Server.Administration.TenantUserResponse[]>("/api/tenant/users");
        var pending = Assert.Single(users!, user => user.Email == "proof@example.com");
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(_application.WebConnectionString);
        await connection.OpenAsync();
        if (useProof)
        {
            await _application.Factory.Services.GetRequiredService<Workbench.Server.Tenancy.TenantContextProof>()
                .ApplyAsync(connection, AuthTestApplication.TenantId, CancellationToken.None);
        }
        await using var transaction = (Microsoft.Data.SqlClient.SqlTransaction)await connection.BeginTransactionAsync();
        await using var claim = new Microsoft.Data.SqlClient.SqlCommand("""
            EXEC [Identity].[ClaimInvitationIdentity] @TenantId=@tenant, @UserId=@user, @TokenHash=@token, @Now=@now;
            """, connection, transaction);
        claim.Parameters.AddWithValue("@tenant", AuthTestApplication.TenantId);
        claim.Parameters.AddWithValue("@user", pending.Id);
        claim.Parameters.AddWithValue("@token", new byte[32]);
        claim.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow);
        // WHEN the owner procedure is called without the required authority.
        var error = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => claim.ExecuteNonQueryAsync());
        // THEN it rejects the claim rather than granting direct directory authority.
        Assert.Equal(expectedError, error.Number);
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
