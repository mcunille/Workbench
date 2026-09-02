// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Server.Administration;
using Workbench.Server.Identity;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class RestoreSanitizationTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task RestoreSanitizationInvalidatesAllAuthenticationArtifacts()
    {
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var admin = application.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await RecoveryTests.PostWithAntiforgeryAsync(
            admin,
            "/api/auth/login",
            new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.AdminPassword }))
            .StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await RecoveryTests.PostWithAntiforgeryAsync(
            admin,
            "/api/auth/recovery",
            new { email = AuthTestApplication.AdminEmail })).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await RecoveryTests.PostWithAntiforgeryAsync(
            admin,
            "/api/tenant/users/invitations",
            new { email = "restore-invite@example.com" })).StatusCode);
        var operations = application.Factory.Services
            .GetRequiredService<DevelopmentIdentityMessageDelivery>()
            .Messages.ToArray();
        Assert.Equal(2, operations.Length);
        Assert.True(await CountAsync(application.AdminConnectionString, "[Identity].[Sessions]") > 0);
        Assert.Equal(2, await CountAsync(application.AdminConnectionString, "[Identity].[IdentityOperations]"));
        Assert.True(await CountAsync(application.AdminConnectionString, "[Identity].[DataProtectionKeys]") > 0);

        var commands = new OperatorCommands(
            application.AdminConnectionString,
            new PasswordHasher<WorkbenchUser>(),
            TimeProvider.System);
        await commands.SanitizeRestoreAsync("restore-test", CancellationToken.None);

        Assert.Equal(0, await CountAsync(application.AdminConnectionString, "[Identity].[Sessions]"));
        Assert.Equal(0, await CountAsync(application.AdminConnectionString, "[Identity].[IdentityOperations]"));
        Assert.Equal(0, await CountAsync(application.AdminConnectionString, "[Identity].[DataProtectionKeys]"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/auth/me")).StatusCode);
        using var anonymous = application.CreateClient();
        foreach (var operation in operations)
        {
            var path = operation.Purpose == IdentityOperationPurpose.Invitation
                ? "/api/auth/invitations/consume"
                : "/api/auth/recovery/consume";
            Assert.Equal(HttpStatusCode.BadRequest, (await RecoveryTests.PostWithAntiforgeryAsync(
                anonymous,
                path,
                new { token = operation.Token, newPassword = "Changed Correct Horse 7!" })).StatusCode);
        }
    }

    private static async Task<int> CountAsync(string connectionString, string table)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"SELECT COUNT(*) FROM {table}", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
