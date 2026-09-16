// Copyright (c) 2026 The White Stag Collection.

using System.Net;
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
        // GIVEN the immediate prior release with valid authentication but no acquisition-document contract.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer, priorMigration: "AddSharedAcquisitions");
        using var client = application.CreateClient();
        // WHEN this release probes readiness.
        var response = await client.GetAsync("/health/ready");
        // THEN it refuses traffic until the additive migration is applied.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await DatabaseMigrator.MigrateAsync(application.AdminConnectionString, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

}
