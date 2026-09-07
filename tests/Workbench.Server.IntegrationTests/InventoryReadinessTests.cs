// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class InventoryReadinessTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task ImmediatePriorSchemaMustBeUpgradedBeforeServingInventory()
    {
        // GIVEN the immediate prior release with valid authentication but no inventory schema.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer, priorMigration: "DeferInvitationIdentityClaim");
        using var client = application.CreateClient();
        // WHEN this release probes readiness.
        var response = await client.GetAsync("/health/ready");
        // THEN it refuses traffic until the additive migration is applied.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await DatabaseMigrator.MigrateAsync(application.AdminConnectionString, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Theory]
    [InlineData("REVOKE INSERT ON [Inventory].[Items] FROM [workbench_web]")]
    [InlineData("DENY SELECT ON [Inventory].[Items] TO [workbench_web]")]
    public async Task MissingInventoryAuthorityPreventsReadiness(string sql)
    {
        // GIVEN the runtime principal loses a required inventory operation.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        await using var connection = new SqlConnection(application.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
        using var client = application.CreateClient();
        // WHEN readiness examines effective grants THEN it refuses traffic.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
    }
}
