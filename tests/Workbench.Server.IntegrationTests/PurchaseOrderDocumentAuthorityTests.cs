// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.Purchasing;
using Xunit;
namespace Workbench.Server.IntegrationTests;

public sealed partial class DraftOrderDatabaseTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("disabled")]
    [InlineData("tenant")]
    [InlineData("replay")]
    public async Task PurchaseDocumentPrepareRequiresCurrentActorAndTenantAuthority(string kind)
    {
        // GIVEN a valid ordered purchase, an enabled tenant and its active owner.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var foreign = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, foreign); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var draft = DraftOrderInput.Normalize(DraftOrderPricingTests.Empty with { SupplierName = "Supplier", Entries = [DraftOrderPricingTests.Line with { Description = "Sapphire" }] });
        var saved = await SaveFinancial(connection, actor, Guid.NewGuid(), JsonNode.Parse(DraftOrderInput.Canonical("Create", null, null, draft))!);
        var committed = await Purchase(connection, actor, saved.Id, Guid.NewGuid(), saved.Version, "Commit", null);
        if (kind == "replay")
        {
            // WHEN another active owner reuses the request THEN original actor evidence cannot be replaced.
            var request = Guid.NewGuid(); await PrepareDocument(connection, saved.Id, actor, request, committed.Version);
            var otherActor = Guid.NewGuid(); await SeedActor(database, tenant, otherActor);
            Assert.Equal(50077, (await Assert.ThrowsAsync<SqlException>(() => PrepareDocument(connection, saved.Id, otherActor, request, committed.Version))).Number);
            return;
        }
        var submittedActor = actor;
        await using var admin = new SqlConnection(database.AdminConnectionString); await admin.OpenAsync();
        if (kind is "missing" or "foreign") submittedActor = Guid.NewGuid();
        if (kind == "foreign") await SeedActor(database, foreign, submittedActor);
        if (kind is "disabled" or "tenant")
        {
            await using var disable = new SqlCommand(kind == "tenant" ? "UPDATE Tenancy.Tenants SET IsEnabled=0 WHERE Id=@id" : "UPDATE [Identity].Users SET State=2 WHERE Id=@id", admin);
            disable.Parameters.AddWithValue("@id", kind == "tenant" ? tenant : actor); await disable.ExecuteNonQueryAsync();
        }
        // WHEN a restricted direct SQL caller attempts reservation with revoked or invented authority.
        var denied = await Assert.ThrowsAsync<SqlException>(() => PrepareDocument(connection, saved.Id, submittedActor, Guid.NewGuid(), committed.Version));
        // THEN authorization fails before creating operation evidence or storage objects.
        Assert.Equal(50403, denied.Number);
        await using var count = new SqlCommand("SELECT (SELECT COUNT(*) FROM Purchasing.PurchaseOrderDocumentOperations)+(SELECT COUNT(*) FROM Storage.Attachments)", admin);
        Assert.Equal(0, await count.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PurchaseDocumentFinalizationRechecksSuspensionAndRetiresPublishedBytes(bool disableTenant)
    {
        // GIVEN an authorized pending upload reservation before provider I/O.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var draft = DraftOrderInput.Normalize(DraftOrderPricingTests.Empty with { SupplierName = "Supplier", Entries = [DraftOrderPricingTests.Line with { Description = "Sapphire" }] });
        var saved = await SaveFinancial(connection, actor, Guid.NewGuid(), JsonNode.Parse(DraftOrderInput.Canonical("Create", null, null, draft))!);
        var committed = await Purchase(connection, actor, saved.Id, Guid.NewGuid(), saved.Version, "Commit", null);
        var request = Guid.NewGuid(); await PrepareDocument(connection, saved.Id, actor, request, committed.Version);
        await using var admin = new SqlConnection(database.AdminConnectionString); await admin.OpenAsync();
        await using var disable = new SqlCommand(disableTenant ? "UPDATE Tenancy.Tenants SET IsEnabled=0 WHERE Id=@id" : "UPDATE [Identity].Users SET State=2 WHERE Id=@id", admin);
        disable.Parameters.AddWithValue("@id", disableTenant ? tenant : actor); await disable.ExecuteNonQueryAsync();
        // WHEN publication returns after suspension THEN finalization fails closed and retains the bytes for cleanup.
        await FinishDocument(connection, request);
        await using var result = new SqlCommand("SELECT (SELECT COUNT(*) FROM Purchasing.PurchaseOrderDocuments),(SELECT State FROM Purchasing.PurchaseOrderDocumentOperations WHERE RequestId=@request),(SELECT COUNT(*) FROM Storage.Attachments WHERE DeletedAtUtc IS NOT NULL),(SELECT COUNT(*) FROM Operations.WorkItems WHERE Kind=1)", admin);
        result.Parameters.AddWithValue("@request", request);
        await using var reader = await result.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt32(0)); Assert.Equal(2, reader.GetInt32(1)); Assert.Equal(1, reader.GetInt32(2)); Assert.Equal(1, reader.GetInt32(3));
    }
}
