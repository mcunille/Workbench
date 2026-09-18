// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.Persistence;
using Workbench.Server.Purchasing;
using Xunit;
namespace Workbench.Server.IntegrationTests;

public sealed partial class DraftOrderDatabaseTests
{
    [Theory]
    [InlineData("piece", "piece", "10.0000", "20.0000", "2.0000", null)]
    [InlineData("piece", "piece", "10.0000", "1.0000", "3.0000", null)]
    [InlineData("piece", "carat", "2.0000", "10.0000", "1.0000", "3.5000")]
    [InlineData("piece", null, "10.0000", null, null, null)]
    [InlineData("piece", "piece", "0.0001", "0.5000", "1.0000", null)]
    [InlineData("piece", "piece", "0.0001", "999999999999999.9999", "0.0001", null)]
    public async Task RetainedCompleteSchemaTwoLinesCommitExactlyAsTheirDisplayedProjection(string unit, string? pricingUnit, string quantity, string? unitPrice, string? denominator, string? pricingQuantity)
    {
        // GIVEN an otherwise valid saved legacy order on the preceding retained schema.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync("IntegrateBetaDraftFinancialAdjustments");
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var line = new StoredDraftEntry(Guid.NewGuid(), "Café / ruby", "Supplier quote", null, null, quantity, unit, unitPrice, pricingUnit, denominator, pricingQuantity, "SKU-1", "Gemstone");
        var legacyContent = JsonSerializer.Serialize(new { sourceLinks = Array.Empty<string>(), entries = new[] { line } }, DraftOrderInput.JsonOptions);
        var draft = DraftOrderInput.Normalize(DraftOrderPricingTests.Empty with { SupplierName = "Supplier", Entries = [DraftOrderInput.Upgrade(line)] });
        Assert.Empty(PurchaseOrderInput.Validate(draft, "2026-09-11", null, false));
        var created = await Save(connection, actor, Guid.NewGuid(), DraftOrderInput.Canonical("Create", null, null, draft), "Create");
        await using var admin = new SqlConnection(database.AdminConnectionString); await admin.OpenAsync();
        await using var fixture = new SqlCommand("UPDATE Purchasing.DraftOrders SET ContentSchemaVersion=2,ContentJson=@content WHERE Id=@id; SELECT RowVersion FROM Purchasing.DraftOrders WHERE Id=@id;", admin);
        fixture.Parameters.AddWithValue("@content", legacyContent); fixture.Parameters.AddWithValue("@id", created.Id);
        var savedVersion = (byte[])(await fixture.ExecuteScalarAsync())!;
        // AND the forward migration preserves the saved legacy bytes and row version.
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        await using var unchanged = new SqlCommand("SELECT ContentJson FROM Purchasing.DraftOrders WHERE Id=@id", connection); unchanged.Parameters.AddWithValue("@id", created.Id);
        Assert.Equal(legacyContent, await unchanged.ExecuteScalarAsync());
        // WHEN committing the same saved version without an invented edit THEN SQL freezes the exact public projection.
        var request = Guid.NewGuid(); var committed = await Purchase(connection, actor, created.Id, request, savedVersion, "Commit", null);
        Assert.Equal(1, committed.Revision);
        await using var read = new SqlCommand("SELECT DraftJson,CalculationJson FROM Purchasing.PurchaseOrderRevisions WHERE DraftOrderId=@id AND Revision=1", connection); read.Parameters.AddWithValue("@id", created.Id);
        await using (var reader = await read.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            var actual = JsonSerializer.Deserialize<DraftContent>(reader.GetString(0), DraftOrderInput.JsonOptions)!;
            Assert.Equal(JsonSerializer.Serialize(draft, DraftOrderInput.JsonOptions), JsonSerializer.Serialize(actual, DraftOrderInput.JsonOptions));
            Assert.Equal(DraftOrderInput.Calculate(draft), JsonSerializer.Deserialize<DraftCalculationResponse>(reader.GetString(1), DraftOrderInput.JsonOptions)!, new CalculationComparer());
        }
        Assert.True((await Purchase(connection, actor, created.Id, request, savedVersion, "Commit", null)).Replayed);
        // WHEN the displayed projection is submitted unchanged through the .NET serializer.
        var unchangedRequest = Guid.NewGuid();
        var noChange = await Assert.ThrowsAsync<SqlException>(() => Purchase(connection, actor, created.Id, unchangedRequest, committed.Version, "Amend", draft));
        // THEN encoding differences create neither a revision nor a receipt and leave the row version unchanged.
        Assert.Equal(50417, noChange.Number);
        await using var evidence = new SqlCommand("SELECT RowVersion,Revision,(SELECT COUNT(*) FROM Purchasing.PurchaseOrderRevisions WHERE DraftOrderId=@id),(SELECT COUNT(*) FROM Purchasing.PurchaseOrderReceipts WHERE RequestId=@request) FROM Purchasing.DraftOrders WHERE Id=@id", connection);
        evidence.Parameters.AddWithValue("@id", created.Id); evidence.Parameters.AddWithValue("@request", unchangedRequest);
        await using var evidenceReader = await evidence.ExecuteReaderAsync();
        Assert.True(await evidenceReader.ReadAsync());
        Assert.Equal(committed.Version, (byte[])evidenceReader[0]);
        Assert.Equal(1, evidenceReader.GetInt32(1));
        Assert.Equal(1, evidenceReader.GetInt32(2));
        Assert.Equal(0, evidenceReader.GetInt32(3));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task IncompleteHistoricalLinesRemainUnchangedWhenCommitmentIsRejected(int schema)
    {
        // GIVEN retained content with an unresolved original quote, never an inferred complete order.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var draft = DraftOrderInput.Normalize(DraftOrderPricingTests.Empty with { SupplierName = "Supplier", Entries = [DraftOrderPricingTests.Line with { Description = "Stone" }] });
        var created = await Save(connection, actor, Guid.NewGuid(), DraftOrderInput.Canonical("Create", null, null, draft), "Create");
        var line = new StoredDraftEntry(draft.Entries[0].Id, "Stone", null, null, schema == 1 ? "3.0000" : null, schema == 1 ? null : "1.0000", schema == 1 ? null : "piece", schema == 1 ? null : "3.0000", null, null, null, null, null);
        var content = JsonSerializer.Serialize(new { sourceLinks = Array.Empty<string>(), entries = new[] { line } }, DraftOrderInput.JsonOptions);
        await using var admin = new SqlConnection(database.AdminConnectionString); await admin.OpenAsync();
        await using var fixture = new SqlCommand("UPDATE Purchasing.DraftOrders SET ContentSchemaVersion=@schema,ContentJson=@content WHERE Id=@id; SELECT RowVersion FROM Purchasing.DraftOrders WHERE Id=@id;", admin);
        fixture.Parameters.AddWithValue("@schema", schema); fixture.Parameters.AddWithValue("@content", content); fixture.Parameters.AddWithValue("@id", created.Id);
        var savedVersion = (byte[])(await fixture.ExecuteScalarAsync())!;
        // WHEN committing through restricted SQL THEN the unresolved original quote is rejected without losing its evidence.
        Assert.Equal(50400, (await Assert.ThrowsAsync<SqlException>(() => Purchase(connection, actor, created.Id, Guid.NewGuid(), savedVersion, "Commit", null))).Number);
        await using var read = new SqlCommand("SELECT ContentJson FROM Purchasing.DraftOrders WHERE Id=@id", connection); read.Parameters.AddWithValue("@id", created.Id);
        Assert.Equal(content, await read.ExecuteScalarAsync());
    }
}
