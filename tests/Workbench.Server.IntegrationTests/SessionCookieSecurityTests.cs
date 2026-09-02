// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Workbench.Server.Identity;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SessionCookieSecurityTests(SqlServerFixture sqlServer)
{
    [Fact]
    public void CookieTicketContainsOnlyOpaqueTokenAndFormatVersion()
    {
        var principal = SessionCookieHandler.CreateCookiePrincipal("opaque-random-token");

        var claims = principal.Claims.OrderBy(claim => claim.Type).ToArray();
        Assert.Equal(2, claims.Length);
        Assert.Equal(SessionCookieHandler.FormatVersionClaimType, claims[0].Type);
        Assert.Equal(SessionCookieHandler.CurrentFormatVersion, claims[0].Value);
        Assert.Equal(SessionCookieHandler.SessionTokenClaimType, claims[1].Type);
        Assert.Equal("opaque-random-token", claims[1].Value);
    }

    [Fact]
    public async Task DevelopmentCookieIsHttpOnlySameSiteAndNotSliding()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(SessionCookieHandler.Scheme);

        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, options.Cookie.SameSite);
        Assert.False(options.SlidingExpiration);
        Assert.Equal(TimeSpan.FromHours(12), options.ExpireTimeSpan);
    }

    [Fact]
    public async Task DataProtectionKeysArePersistedInSqlWhenDatabaseIsConfigured()
    {
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var proofKey = await database.GetTenantContextProofKeyAsync();
        var webConnection = await database.CreateWebUserAsync();
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:Workbench", webConnection);
                builder.UseSetting("TenantContext:ProofKey", Convert.ToBase64String(proofKey));
            });

        using var client = factory.CreateClient();
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/api/system")).StatusCode);

        await using var connection = new SqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM [Identity].[DataProtectionKeys]",
            connection);
        Assert.True(Convert.ToInt32(await command.ExecuteScalarAsync()) > 0);
    }
}
