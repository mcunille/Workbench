// Copyright (c) 2026 The White Stag Collection.
using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Server.Tenancy;
using Workbench.Server.Administration;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;
namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class AccountingRoleTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task TenantAdministratorCanReadTheTwoUnassignedAccountingRoles()
    {
        // GIVEN an enabled tenant administrator with no accounting membership.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = await LoginAsync(application, AuthTestApplication.AdminEmail);
        // WHEN reading the accounting role catalog and their own assignments.
        var roles = await client.GetFromJsonAsync<AccountingRoleResponse[]>("/api/beta/tenant/accounting-roles");
        var assignment = await ReadAsync(client, AuthTestApplication.AdminUserId);
        // THEN only two fixed roles exist and no accounting access was silently assigned.
        Assert.Equal(2, roles!.Length);
        Assert.Equal(7, Assert.Single(roles, role => role.Name == "Accounting administrator").Permissions.Length);
        Assert.Equal(2, Assert.Single(roles, role => role.Name == "Accounting reader").Permissions.Length);
        Assert.Empty(assignment.RoleIds);
        Assert.Equal(Guid.Empty.ToString(), assignment.Version);
    }

    [Fact]
    public async Task AssignmentRevocationAndReplayPreserveCurrentMembershipAndRejectStaleWrites()
    {
        // GIVEN a tenant administrator and a previously authenticated ordinary member.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var admin = await LoginAsync(application, AuthTestApplication.AdminEmail);
        using var member = await LoginAsync(application, "member@example.com");
        var roles = await admin.GetFromJsonAsync<AccountingRoleResponse[]>("/api/beta/tenant/accounting-roles");
        var administrator = Assert.Single(roles!, role => role.Name == "Accounting administrator");
        var initial = await ReadAsync(admin, AuthTestApplication.MemberUserId);
        var grant = new AccountingRoleAssignmentRequest(Guid.NewGuid(), initial.Version, [administrator.Id]);
        // WHEN granting accounting authority.
        var response = await WriteAsync(admin, AuthTestApplication.MemberUserId, grant);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var granted = (await response.Content.ReadFromJsonAsync<AccountingRoleAssignmentResponse>())!;
        // THEN the already authenticated user's next request sees the grant, without user administration authority.
        Assert.Contains("AccountingConfigurationManage", await member.GetStringAsync("/api/beta/auth/me"));
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/beta/tenant/accounting-roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await WriteAsync(admin, AuthTestApplication.MemberUserId,
            new(Guid.NewGuid(), initial.Version, []))).StatusCode);
        // WHEN revoking and replaying the old successful grant.
        Assert.Equal(HttpStatusCode.OK, (await WriteAsync(admin, AuthTestApplication.MemberUserId,
            new(Guid.NewGuid(), granted.Version, []))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await WriteAsync(admin, AuthTestApplication.MemberUserId, grant)).StatusCode);
        // THEN replay returns evidence but cannot restore the revoked grant.
        Assert.Empty((await ReadAsync(admin, AuthTestApplication.MemberUserId)).RoleIds);
        Assert.DoesNotContain("AccountingConfigurationManage", await member.GetStringAsync("/api/beta/auth/me"));
        Assert.Equal(HttpStatusCode.Conflict, (await WriteAsync(admin, AuthTestApplication.MemberUserId,
            grant with { RoleIds = [] })).StatusCode);
    }

    [Fact]
    public async Task ArbitraryRolesForeignUsersAndDisabledUsersCannotBeAssigned()
    {
        // GIVEN a tenant administrator and a legitimate accounting reader role.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var admin = await LoginAsync(application, AuthTestApplication.AdminEmail);
        var roles = await admin.GetFromJsonAsync<AccountingRoleResponse[]>("/api/beta/tenant/accounting-roles");
        var reader = Assert.Single(roles!, role => role.Name == "Accounting reader");
        // WHEN trying a foreign user, disabled user, or non-accounting role.
        foreach (var (user, role, status) in new[] {
            (AuthTestApplication.OtherTenantUserId, reader.Id, HttpStatusCode.NotFound),
            (AuthTestApplication.DisabledUserId, reader.Id, HttpStatusCode.BadRequest),
            (AuthTestApplication.MemberUserId, Guid.Parse("22222222-2222-2222-2222-222222222222"), HttpStatusCode.BadRequest) })
        {
            var response = await WriteAsync(admin, user, new(Guid.NewGuid(), Guid.Empty.ToString(), [role]));
            // THEN the command rejects each attempt without granting membership.
            Assert.Equal(status, response.StatusCode);
        }
        Assert.Empty((await ReadAsync(admin, AuthTestApplication.MemberUserId)).RoleIds);
    }

    [Fact]
    public async Task UserClaimsCannotGrantAccountingAuthorityAndWebCannotWriteRoleStorage()
    {
        // GIVEN an accounting permission injected as a user claim by a privileged fixture administrator.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        await using (var connection = new SqlConnection(application.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var insert = new SqlCommand("INSERT [Identity].UserClaims(TenantId,UserId,ClaimType,ClaimValue) VALUES(@tenant,@user,N'workbench/permission',N'AccountingConfigurationManage')", connection);
            insert.Parameters.AddWithValue("@tenant", AuthTestApplication.TenantId);
            insert.Parameters.AddWithValue("@user", AuthTestApplication.MemberUserId);
            await insert.ExecuteNonQueryAsync();
        }
        // WHEN the member resolves a session.
        using var member = await LoginAsync(application, "member@example.com");
        // THEN user claims do not confer accounting authority.
        Assert.DoesNotContain("AccountingConfigurationManage", await member.GetStringAsync("/api/beta/auth/me"));
        await using var web = new SqlConnection(application.WebConnectionString);
        await web.OpenAsync();
        foreach (var table in new[] { "Roles", "RoleClaims", "UserRoles", "UserClaims" })
        {
            // AND the actual runtime principal cannot bypass the restricted command.
            await using var command = new SqlCommand($"DELETE FROM [Identity].{table} WHERE 1=0", web);
            var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(229, exception.Number);
        }
    }

    [Fact]
    public async Task CompetingAssignmentsHaveExactlyOneWinner()
    {
        // GIVEN two administrators' drafts based on the same membership version.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var admin = await LoginAsync(application, AuthTestApplication.AdminEmail);
        using var otherRequest = await LoginAsync(application, AuthTestApplication.AdminEmail);
        var roles = await admin.GetFromJsonAsync<AccountingRoleResponse[]>("/api/beta/tenant/accounting-roles");
        // WHEN both commands execute concurrently.
        var results = await Task.WhenAll(
            WriteAsync(admin, AuthTestApplication.MemberUserId, new(Guid.NewGuid(), Guid.Empty.ToString(), [roles![0].Id])),
            WriteAsync(otherRequest, AuthTestApplication.MemberUserId, new(Guid.NewGuid(), Guid.Empty.ToString(), [roles[1].Id])));
        // THEN only one version wins; the losing update never silently overwrites it.
        Assert.Single(results, result => result.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, result => result.StatusCode == HttpStatusCode.Conflict);
        var winner = (await results.Single(result => result.StatusCode == HttpStatusCode.OK)
            .Content.ReadFromJsonAsync<AccountingRoleAssignmentResponse>())!;
        var current = await ReadAsync(admin, AuthTestApplication.MemberUserId);
        Assert.Equal(winner.Version, current.Version);
        Assert.Equal(winner.RoleIds, current.RoleIds);
    }

    [Fact]
    public async Task RestrictedCommandRechecksRevokedSessionBeforeReturningReplayReceipt()
    {
        // GIVEN a successful role command from a session that was subsequently revoked.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var admin = await LoginAsync(application, AuthTestApplication.AdminEmail);
        var request = new AccountingRoleAssignmentRequest(Guid.NewGuid(), Guid.Empty.ToString(), []);
        Assert.Equal(HttpStatusCode.OK, (await WriteAsync(admin, AuthTestApplication.MemberUserId, request)).StatusCode);
        Guid session;
        await using (var privileged = new SqlConnection(application.AdminConnectionString))
        {
            await privileged.OpenAsync();
            await using var revoke = new SqlCommand("UPDATE [Identity].Sessions SET RevokedAtUtc=SYSUTCDATETIME() OUTPUT inserted.Id WHERE UserId=@user", privileged);
            revoke.Parameters.AddWithValue("@user", AuthTestApplication.AdminUserId);
            session = (Guid)(await revoke.ExecuteScalarAsync())!;
        }
        // WHEN a previously admitted request invokes the restricted command directly with that session.
        await using var web = new SqlConnection(application.WebConnectionString);
        await web.OpenAsync();
        await application.Factory.Services.GetRequiredService<TenantContextProof>().ApplyAsync(web, AuthTestApplication.TenantId, default);
        await using var command = new SqlCommand("EXEC Administration.AssignAccountingRoles @ActorId,@SessionId,@UserId,@RequestId,@ExpectedVersion,N'[]'", web);
        command.Parameters.AddWithValue("@ActorId", AuthTestApplication.AdminUserId);
        command.Parameters.AddWithValue("@SessionId", session);
        command.Parameters.AddWithValue("@UserId", AuthTestApplication.MemberUserId);
        command.Parameters.AddWithValue("@RequestId", request.RequestId);
        command.Parameters.AddWithValue("@ExpectedVersion", Guid.Empty);
        // THEN even an existing receipt cannot bypass current authority.
        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(50903, exception.Number);
    }
    [Fact]
    public async Task ConcurrentDisableAndAssignmentCannotLeaveEffectiveAccountingAuthority()
    {
        // GIVEN an enabled member and separate administrator requests.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var admin = await LoginAsync(application, AuthTestApplication.AdminEmail);
        using var disableClient = await LoginAsync(application, AuthTestApplication.AdminEmail);
        using var member = await LoginAsync(application, "member@example.com");
        var roles = await admin.GetFromJsonAsync<AccountingRoleResponse[]>("/api/beta/tenant/accounting-roles");
        var tokens = await disableClient.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/beta/auth/antiforgery");
        using var disable = new HttpRequestMessage(HttpMethod.Delete, $"/api/beta/tenant/users/{AuthTestApplication.MemberUserId}");
        disable.Headers.Add("X-CSRF-TOKEN", tokens.GetProperty("requestToken").GetString());
        // WHEN disabling and assigning race at the server.
        var disabling = disableClient.SendAsync(disable);
        var assigning = WriteAsync(admin, AuthTestApplication.MemberUserId, new(Guid.NewGuid(), Guid.Empty.ToString(), [roles![0].Id]));
        await Task.WhenAll(disabling, assigning);
        // THEN both serialize without server errors, and disable always revokes effective access.
        Assert.Equal(HttpStatusCode.NoContent, (await disabling).StatusCode);
        Assert.Contains((await assigning).StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest });
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/api/beta/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await WriteAsync(admin, AuthTestApplication.MemberUserId,
            new(Guid.NewGuid(), (await ReadAsync(admin, AuthTestApplication.MemberUserId)).Version, []))).StatusCode);
    }
    [Fact]
    public async Task UpgradeAddsRoleDefinitionsWithoutGrantingExistingUsersAccountingAccess()
    {
        // GIVEN tenants and users created under the previous production schema.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer,
            priorMigration: "20260921041331_MakeSupplierProfilesCustom");
        // WHEN upgrading the existing database.
        await Workbench.Server.Persistence.DatabaseMigrator.MigrateAsync(application.AdminConnectionString, default);
        using var admin = await LoginAsync(application, AuthTestApplication.AdminEmail);
        var roles = await admin.GetFromJsonAsync<AccountingRoleResponse[]>("/api/beta/tenant/accounting-roles");
        // THEN both fixed roles exist, and earlier administrators retain only their earlier authority.
        Assert.Equal(2, roles!.Length);
        Assert.Empty((await ReadAsync(admin, AuthTestApplication.AdminUserId)).RoleIds);
        Assert.DoesNotContain("AccountingConfigurationManage", await admin.GetStringAsync("/api/beta/auth/me"));
    }
    private static async Task<HttpClient> LoginAsync(AuthTestApplication application, string email)
    {
        var client = application.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await RecoveryTests.PostWithAntiforgeryAsync(client,
            "/api/beta/auth/login", new { email, password = AuthTestApplication.AdminPassword })).StatusCode);
        return client;
    }
    private static async Task<AccountingRoleAssignmentResponse> ReadAsync(HttpClient client, Guid userId) =>
        (await client.GetFromJsonAsync<AccountingRoleAssignmentResponse>($"/api/beta/tenant/users/{userId}/accounting-roles"))!;
    private static Task<HttpResponseMessage> WriteAsync(HttpClient client, Guid userId, AccountingRoleAssignmentRequest request) =>
        RecoveryTests.PostWithAntiforgeryAsync(client, $"/api/beta/tenant/users/{userId}/accounting-roles", request);
}
