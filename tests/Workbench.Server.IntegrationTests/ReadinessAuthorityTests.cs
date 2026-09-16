// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ReadinessAuthorityTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData("REVOKE EXECUTE ON [Purchasing].[CreateDraftOrderV2] FROM [workbench_web]")]
    [InlineData("REVOKE EXECUTE ON [Purchasing].[UpdateDraftOrderV2] FROM [workbench_web]")]
    [InlineData("REVOKE EXECUTE ON [Purchasing].[SaveSupplier] FROM [workbench_web]")]
    [InlineData("REVOKE SELECT ON [Purchasing].[Suppliers] FROM [workbench_web]")]
    [InlineData("REVOKE SELECT ON [Purchasing].[SupplierRequestReceipts] FROM [workbench_web]")]
    [InlineData("REVOKE SELECT ON [Purchasing].[PurchaseOrderCounters] FROM [workbench_web]")]
    [InlineData("GRANT UPDATE ON [Purchasing].[Suppliers] TO [workbench_web]")]
    [InlineData("GRANT UPDATE ON [Purchasing].[SupplierRequestReceipts] TO [workbench_web]")]
    [InlineData("GRANT UPDATE ON [Purchasing].[PurchaseOrderCounters] TO [workbench_web]")]
    [InlineData("REVOKE EXECUTE ON [Purchasing].[CreateDraftOrder] FROM [workbench_web]")]
    [InlineData("REVOKE EXECUTE ON [Purchasing].[UpdateDraftOrder] FROM [workbench_web]")]
    [InlineData("REVOKE EXECUTE ON [Purchasing].[DeleteDraftOrder] FROM [workbench_web]")]
    [InlineData("REVOKE SELECT ON [Purchasing].[DraftOrders] FROM [workbench_web]")]
    [InlineData("REVOKE SELECT ON [Purchasing].[DraftOrderRequestReceipts] FROM [workbench_web]")]
    [InlineData("GRANT UPDATE ON [Purchasing].[DraftOrders] TO [workbench_web]")]
    public async Task MissingDraftOrderBoundariesMakeReadinessUnhealthy(string changeAuthority)
    {
        // WHEN a required procedure or effective authority is changed in an otherwise healthy database.
        var status = await CheckAfterChangeAsync(changeAuthority);
        // THEN the real readiness service rejects the deployment under its restricted web principal.
        Assert.Equal(HealthStatus.Unhealthy, status);
    }

    [Theory]
    [InlineData("DROP PROCEDURE [Operations].[ReadWorkQueueStatus]")]
    [InlineData("REVOKE EXECUTE ON [Operations].[ReadWorkQueueStatus] FROM [workbench_worker]")]
    [InlineData("DROP PROCEDURE [Security].[ReadProviderRetryReadiness]")]
    [InlineData("REVOKE EXECUTE ON [Operations].[RetryWork] FROM [workbench_worker]")]
    [InlineData("ALTER PROCEDURE [Operations].[RetryWork] AS SELECT 0;")]
    public async Task MissingQueueTelemetryAuthorityMakesReadinessUnhealthy(string breakTelemetry)
    {
        // WHEN a required procedure or effective authority is changed in an otherwise healthy database.
        var status = await CheckAfterChangeAsync(breakTelemetry);
        // THEN the real readiness service rejects the deployment under its restricted web principal.
        Assert.Equal(HealthStatus.Unhealthy, status);
    }

    [Theory]
    [InlineData("DROP PROCEDURE [Identity].[ClaimInvitationIdentity]")]
    [InlineData("REVOKE EXECUTE ON [Identity].[ClaimInvitationIdentity] FROM [workbench_web]")]
    [InlineData("DENY EXECUTE ON [Identity].[ClaimInvitationIdentity] TO [workbench_web]")]
    [InlineData("GRANT VIEW DEFINITION ON [Identity].[ClaimInvitationIdentity] TO [workbench_web]; DENY EXECUTE ON [Identity].[ClaimInvitationIdentity] TO [workbench_web]")]
    public async Task MissingInvitationClaimAuthorityMakesReadinessUnhealthy(string breakInvitation)
    {
        // WHEN a required procedure or effective authority is changed in an otherwise healthy database.
        var status = await CheckAfterChangeAsync(breakInvitation);
        // THEN the real readiness service rejects the deployment under its restricted web principal.
        Assert.Equal(HealthStatus.Unhealthy, status);
    }

    [Theory]
    [InlineData("REVOKE INSERT ON [Inventory].[Items] FROM [workbench_web]")]
    [InlineData("DENY SELECT ON [Inventory].[Items] TO [workbench_web]")]
    [InlineData("DENY EXECUTE ON [Inventory].[UpdateItemDetails] TO [workbench_web]")]
    [InlineData("DENY EXECUTE ON [Inventory].[ArchiveItem] TO [workbench_web]")]
    [InlineData("DENY EXECUTE ON [Inventory].[RestoreItem] TO [workbench_web]")]
    [InlineData("DENY SELECT ON [Inventory].[ItemCreationSnapshots] TO [workbench_web]")]
    [InlineData("DENY EXECUTE ON [Inventory].[CreateAcquisition] TO [workbench_web]")]
    [InlineData("DENY EXECUTE ON [Inventory].[UpdateAcquisition] TO [workbench_web]")]
    [InlineData("DENY EXECUTE ON [Inventory].[ChangeAcquisitionLink] TO [workbench_web]")]
    [InlineData("DENY SELECT ON [Inventory].[Acquisitions] TO [workbench_web]")]
    [InlineData("DENY SELECT ON [Inventory].[AcquisitionItems] TO [workbench_web]")]
    [InlineData("DENY SELECT ON [Inventory].[AcquisitionCreationRecords] TO [workbench_web]")]
    [InlineData("GRANT UPDATE ON [Inventory].[Acquisitions] TO [workbench_web]")]
    [InlineData("GRANT INSERT ON [Inventory].[AcquisitionItems] TO [workbench_web]")]
    [InlineData("GRANT DELETE ON [Inventory].[AcquisitionCreationRecords] TO [workbench_web]")]
    [InlineData("DENY EXECUTE ON [Inventory].[PrepareAcquisitionDocument] TO [workbench_web]")]
    [InlineData("DENY EXECUTE ON [Inventory].[FinishAcquisitionDocument] TO [workbench_web]")]
    [InlineData("GRANT UPDATE ON [Inventory].[AcquisitionDocuments] TO [workbench_web]")]
    [InlineData("GRANT INSERT ON [Inventory].[AcquisitionDocumentOperations] TO [workbench_web]")]
    [InlineData("DENY SELECT ON [Inventory].[AcquisitionDocuments] TO [workbench_web]")]
    public async Task MissingInventoryAuthorityPreventsReadiness(string sql)
    {
        // WHEN a required procedure or effective authority is changed in an otherwise healthy database.
        var status = await CheckAfterChangeAsync(sql);
        // THEN the real readiness service rejects the deployment under its restricted web principal.
        Assert.Equal(HealthStatus.Unhealthy, status);
    }

    private async Task<HealthStatus> CheckAfterChangeAsync(string sql)
    {
        // GIVEN a unique current-schema database whose actual web authority is healthy before the mutation.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var web = await database.CreateWebUserAsync();
        var proof = new TenantContextProof(await database.GetTenantContextProofKeyAsync());
        var check = new DatabaseReadinessCheck(web, proof);
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext())).Status);
        // WHEN its migration-defined authority or required procedure changes.
        await using (var connection = new SqlConnection(database.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        // THEN evaluate the production readiness service again without HTTP or authentication seed overhead.
        return (await check.CheckHealthAsync(new HealthCheckContext())).Status;
    }
}
