// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SessionCookieSecurityTests(SqlServerFixture sqlServer)
{

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
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/api/beta/system")).StatusCode);

        await using var connection = new SqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM [Identity].[DataProtectionKeys]",
            connection);
        Assert.True(Convert.ToInt32(await command.ExecuteScalarAsync()) > 0);
    }
}
