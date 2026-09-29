// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class DatabaseSchemaReadinessTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData("AddBlobAndOperationalProviders")]
    [InlineData("20260928034802_AddSupplierBills")]
    public async Task PriorReleaseSchemaIsUnreadyUntilDeploymentMigrationIsApplied(string priorMigration)
    {
        // GIVEN an early schema or the immediate predecessor of the current release.
        // ReadinessAuthorityTests independently probes required authorities from a healthy baseline.
        await using var prior = await AuthTestApplication.CreateAsync(sqlServer, priorMigration: priorMigration);
        using var client = prior.CreateClient();
        // WHEN the current application probes that older schema.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        // THEN applying the required deployment migration makes this release ready.
        await DatabaseMigrator.MigrateAsync(prior.AdminConnectionString, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }
}
