// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Workbench.Server.Administration;
using Workbench.Server.Identity;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.ServiceAdministration;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ServiceAdminRestoreTests(SqlServerFixture sqlServer)
{
    private const string Prefix = "/api/beta/service-admin/auth";

    [Theory]
    [InlineData("DisableServiceAdmin", false)]
    [InlineData("RevokeServiceAdminSessions", false)]
    [InlineData("DisableServiceAdmin", true)]
    public async Task GuardedRestoreCannotResurrectPreBackupAuthority(string operation, bool disabledInBackup)
    {
        // GIVEN a signed-in service admin and a real checksum backup in this fixture's disposable SQL container.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        await application.ProvisionServiceAdminAsync();
        var operatorConnection = await application.CreateOperatorConnectionAsync();
        using var client = application.CreateClient();
        var login = await LoginAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';')[0];
        var options = application.Factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(ServiceAdminCookieHandler.Scheme);
        var ticket = options.TicketDataFormat.Unprotect(Uri.UnescapeDataString(cookie[(cookie.IndexOf('=') + 1)..]))!;
        var token = ticket.Principal.FindFirst(ServiceAdminCookieHandler.SessionTokenClaimType)!.Value;
        if (disabledInBackup) await application.MaintainServiceAdminAsync(operation);
        var before = await ScalarAsync<long>(application.AdminConnectionString, "SELECT SecurityVersion FROM ServiceAdministration.Accounts");
        var target = new SqlConnectionStringBuilder(application.AdminConnectionString);
        var name = target.InitialCatalog;
        Assert.Matches("^workbench_test_[a-f0-9]+$", name);
        var path = $"/var/opt/mssql/data/{name}_admin_restore.bak";
        await ExecuteAsync(target.ConnectionString, $"BACKUP DATABASE [{name}] TO DISK=@path WITH COPY_ONLY,CHECKSUM,INIT", path);

        // WHEN later disablement or revocation ends authority and the earlier backup is restored.
        if (!disabledInBackup) await application.MaintainServiceAdminAsync(operation);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ReplayAsync(client, cookie)).StatusCode);
        var master = new SqlConnectionStringBuilder(target.ConnectionString) { InitialCatalog = "master", Pooling = false };
        SqlConnection.ClearAllPools();
        await ExecuteAsync(master.ConnectionString, $"""
            ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
            RESTORE DATABASE [{name}] FROM DISK=@path WITH REPLACE,CHECKSUM,RECOVERY,RESTRICTED_USER;
            USE [{name}]; EXEC Administration.MarkRestorePending;
            """, path);
        target.Pooling = false;
        // THEN an independent connection proves restricted access and the pending guard before release.
        Assert.True(await ScalarAsync<bool>(target.ConnectionString, "SELECT IsPending FROM Security.WorkbenchRestorePending"));
        Assert.Equal("RESTRICTED_USER", await ScalarAsync<string>(target.ConnectionString, "SELECT user_access_desc FROM sys.databases WHERE database_id=DB_ID()"));
        await ExecuteAsync(master.ConnectionString, $"ALTER DATABASE [{name}] SET MULTI_USER");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);

        // WHEN the actual operator principal performs mandatory sanitation.
        var commands = new OperatorCommands(operatorConnection, new PasswordHasher<WorkbenchUser>(), TimeProvider.System);
        await commands.SanitizeRestoreAsync("service-admin-disposable-restore", CancellationToken.None);

        // THEN cached-key and fresh-key hosts reject the retained cookie, and the raw token has no authority.
        Assert.Equal(HttpStatusCode.Unauthorized, (await ReplayAsync(client, cookie)).StatusCode);
        await using var fresh = application.Factory.WithWebHostBuilder(_ => { });
        using var freshClient = fresh.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await ReplayAsync(freshClient, cookie)).StatusCode);
        using var scope = application.Factory.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<ServiceAdminSessionService>().ResolveAsync(token, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(0, await ScalarAsync<int>(target.ConnectionString, "SELECT COUNT(*) FROM ServiceAdministration.Sessions"));
        Assert.Equal(before + 1, await ScalarAsync<long>(target.ConnectionString, "SELECT SecurityVersion FROM ServiceAdministration.Accounts"));
        Assert.Equal(!disabledInBackup, await ScalarAsync<bool>(target.ConnectionString, "SELECT IsEnabled FROM ServiceAdministration.Accounts"));
        Assert.False(await ScalarAsync<bool>(target.ConnectionString, "SELECT IsPending FROM Security.WorkbenchRestorePending"));
        Assert.Equal(0, await ScalarAsync<int>(target.ConnectionString, "SELECT COUNT(*) FROM [Identity].Sessions"));
        Assert.False(await ScalarAsync<bool>(target.ConnectionString, "SELECT IsPending FROM Security.BlobRecoveryState"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        // AND only fresh sign-in under the restored enabled state can obtain new authority.
        using var newClient = application.CreateClient();
        Assert.Equal(disabledInBackup ? HttpStatusCode.Unauthorized : HttpStatusCode.NoContent, (await LoginAsync(newClient)).StatusCode);
        Assert.Equal(disabledInBackup ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, (await newClient.GetAsync(Prefix + "/me")).StatusCode);
    }

    [Fact]
    public async Task FailedSanitationAuditRollsBackEveryInvalidationAndKeepsReadinessPending()
    {
        // GIVEN both authorities, persisted keys and a pending restore with an injected final audit-write failure.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        await application.ProvisionServiceAdminAsync();
        using var admin = application.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await LoginAsync(admin)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await RecoveryTests.PostWithAntiforgeryAsync(admin, "/api/beta/auth/login",
            new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.AdminPassword })).StatusCode);
        var operatorConnection = await application.CreateOperatorConnectionAsync();
        var disabledAccount = await ServiceAdminIdentityDatabaseTests.ProvisionAsync(operatorConnection, Guid.NewGuid(), "disabled-service-admin@example.com");
        await ServiceAdminIdentityDatabaseTests.ScalarAsync<object>(operatorConnection,
            "EXEC Administration.DisableServiceAdmin @AccountId=@id,@Now=@now", ("id", disabledAccount), ("now", DateTimeOffset.UtcNow));
        await ExecuteAsync(application.AdminConnectionString, "EXEC Administration.MarkRestorePending");
        var before = await SnapshotAsync(application.AdminConnectionString);
        await ExecuteAsync(application.AdminConnectionString, """
            CREATE TRIGGER Security.RejectSanitation ON Security.SystemSecurityAuditEvents AFTER INSERT AS
                IF EXISTS(SELECT 1 FROM inserted WHERE Action=N'database.restore-sanitized')
                    THROW 50042,'Injected sanitation failure.',1;
            """);
        // WHEN the operator attempts sanitation and the final audit write fails after marker clearance.
        var commands = new OperatorCommands(operatorConnection, new PasswordHasher<WorkbenchUser>(), TimeProvider.System);
        var failure = await Assert.ThrowsAsync<SqlException>(() => commands.SanitizeRestoreAsync("atomic-admin-restore", CancellationToken.None));
        // THEN no deletion, version, generation, audit or marker change commits.
        Assert.Equal(50042, failure.Number);
        Assert.Equal(before, await SnapshotAsync(application.AdminConnectionString));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await admin.GetAsync("/health/ready")).StatusCode);
        // WHEN the obstruction is removed THEN the same operator can finish sanitation atomically.
        await ExecuteAsync(application.AdminConnectionString, "DROP TRIGGER Security.RejectSanitation");
        await commands.SanitizeRestoreAsync("atomic-admin-restore", CancellationToken.None);
        Assert.Equal(0, await ScalarAsync<int>(application.AdminConnectionString, "SELECT COUNT(*) FROM ServiceAdministration.Sessions"));
        Assert.Equal(5L, await ScalarAsync<long>(application.AdminConnectionString, "SELECT SUM(SecurityVersion) FROM ServiceAdministration.Accounts"));
        Assert.Equal(1, await ScalarAsync<int>(application.AdminConnectionString, "SELECT COUNT(*) FROM ServiceAdministration.Accounts WHERE IsEnabled=0"));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/health/ready")).StatusCode);
    }

    private static Task<string> SnapshotAsync(string connection) => ScalarAsync<string>(connection, """
        SELECT (SELECT COUNT(*) FROM ServiceAdministration.Sessions) AdminSessions,
            (SELECT SUM(SecurityVersion) FROM ServiceAdministration.Accounts) AdminVersions,
            (SELECT COUNT(*) FROM [Identity].Sessions) TenantSessions,
            (SELECT SUM(SecurityVersion) FROM [Identity].Users) TenantVersions,
            (SELECT COUNT(*) FROM [Identity].DataProtectionKeys) Keys,
            (SELECT COUNT(*) FROM Security.SystemSecurityAuditEvents) Audit,
            (SELECT RestoreGeneration FROM Security.DatabaseSecurityState) Generation,
            (SELECT RestoreSanitizedGeneration FROM Security.DatabaseSecurityState) Sanitized,
            (SELECT IsPending FROM Security.WorkbenchRestorePending) Pending
        FOR JSON PATH,WITHOUT_ARRAY_WRAPPER
        """);

    internal static async Task<HttpResponseMessage> LoginAsync(HttpClient client)
    {
        var bootstrap = await client.GetFromJsonAsync<AntiforgeryResponse>(Prefix + "/antiforgery");
        using var request = new HttpRequestMessage(HttpMethod.Post, Prefix + "/login")
        { Content = JsonContent.Create(new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.ServiceAdminPassword }) };
        request.Headers.Add("X-CSRF-TOKEN", bootstrap!.RequestToken);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> ReplayAsync(HttpClient client, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Prefix + "/me");
        request.Headers.Add("Cookie", cookie);
        return await client.SendAsync(request);
    }

    internal static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    internal static async Task ExecuteAsync(string connectionString, string sql, string? path = null)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        if (path is not null) command.Parameters.AddWithValue("@path", path);
        await command.ExecuteNonQueryAsync();
    }
}
