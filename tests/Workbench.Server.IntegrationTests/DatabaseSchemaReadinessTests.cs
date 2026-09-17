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
    [InlineData("AddDeploymentQueueTelemetry")]
    [InlineData("DeferInvitationIdentityClaim")]
    [InlineData("AddProviderRetryDelay")]
    [InlineData("AddAcquisitionDocuments")]
    [InlineData("AddDraftSupplierOrders")]
    [InlineData("AddSupplierIdentityAndPurchaseReferences")]
    [InlineData("AddSupplierBasedDraftPricing")]
    [InlineData("ConsolidateBetaDraftCommands")]
    public async Task PriorReleaseSchemaIsUnreadyUntilDeploymentMigrationIsApplied(string priorMigration)
    {
        // GIVEN a prior release schema lacks one of this release's required worker or identity capabilities.
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
