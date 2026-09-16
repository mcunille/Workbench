// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using System.Text.Json.Nodes;
using Xunit;
namespace Workbench.Server.IntegrationTests;

public sealed partial class DraftOrderDatabaseTests
{
    [Fact]
    public async Task UpgradePreservesLegacyReceiptsAndRejectsNewOldContractWrites()
    {
        // GIVEN the PO-02 schema with a saved draft and its original request bytes.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync("AddSupplierIdentityAndPurchaseReferences");
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid(); var request = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var legacy = JsonNode.Parse(Canonical("Create", null, null, "Legacy"))!;
        legacy["draft"]!["currency"] = "USD";
        legacy["draft"]!["entries"] = new JsonArray(JsonNode.Parse("""{"id":"3923d8c7-b0ad-4765-9de7-7d6619f00fd4","description":null,"notes":null,"sourceLink":null,"indicativePrice":"12.0000"}"""));
        var json = legacy.ToJsonString();
        async Task<Guid> LegacySave(Guid requestId, string body)
        {
            await using var command = new SqlCommand("Purchasing.CreateDraftOrderV2", connection) { CommandType = System.Data.CommandType.StoredProcedure };
            command.Parameters.AddWithValue("@RequestId", requestId); command.Parameters.AddWithValue("@ActorUserId", actor); command.Parameters.AddWithValue("@CanonicalInputJson", body);
            await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); return reader.GetGuid(reader.GetOrdinal("DraftOrderId"));
        }
        var id = await LegacySave(request, json);
        // WHEN the forward migration runs THEN original successful retries remain valid.
        await Workbench.Server.Persistence.DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        Assert.Equal(id, await LegacySave(request, json));
        // AND changed request bytes conflict while unmatched legacy writes demand reload.
        Assert.Equal(50410, (await Assert.ThrowsAsync<SqlException>(() => LegacySave(request, Canonical("Create", null, null, "Changed")))).Number);
        Assert.Equal(50426, (await Assert.ThrowsAsync<SqlException>(() => LegacySave(Guid.NewGuid(), json))).Number);
        // AND converting and deleting a draft preserve its permanent number and original receipts.
        await using var read = new SqlCommand("SELECT RowVersion FROM Purchasing.DraftOrders WHERE Id=@id", connection);
        read.Parameters.AddWithValue("@id", id); var version = (byte[])(await read.ExecuteScalarAsync())!;
        var updateRequest = Guid.NewGuid(); var converted = JsonNode.Parse(Canonical("Update", id, version, "Structured"))!;
        converted["draft"]!["currency"] = "USD"; var updatedJson = converted.ToJsonString();
        var updated = await Save(connection, actor, updateRequest, updatedJson, "Update");
        await Delete(connection, actor, Guid.NewGuid(), id, updated.Version);
        Assert.True((await Save(connection, actor, updateRequest, updatedJson, "Update")).Replayed);
        Assert.Equal(id, await LegacySave(request, json));
    }
    [Fact]
    public async Task StructuredLinesPersistAndRejectInvalidQuantitiesAtRestrictedBoundary()
    {
        // GIVEN a tenant-authorized restricted writer and an explicitly priced line.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var input = JsonNode.Parse(Canonical("Create", null, null, "Structured"))!;
        input["draft"]!["currency"] = "USD";
        input["draft"]!["entries"] = new JsonArray(JsonNode.Parse("""
            {"id":"3923d8c7-b0ad-4765-9de7-7d6619f00fd4","description":null,"notes":null,"sourceLink":null,"indicativePrice":null,"quantity":"10.0000","unitOfMeasure":"piece","unitPrice":"20.0000","pricingUnit":"carat","pricePerQuantity":"1.0000","pricingQuantity":"12.5000","supplierSku":"SKU-1","itemType":"Gemstone"}
            """));
        // WHEN saved through V3 THEN the complete basis is stored with the new schema and fingerprint.
        var saved = await SaveV3(input.ToJsonString());
        await using var read = new SqlCommand("SELECT ContentSchemaVersion FROM Purchasing.DraftOrders WHERE Id=@id", connection);
        read.Parameters.AddWithValue("@id", saved);
        Assert.Equal(2, Convert.ToInt32(await read.ExecuteScalarAsync()));
        // AND malformed supplied quantities cannot bypass HTTP validation or create receipts.
        foreach (var value in new[] { "0.0000", "-1.0000", "1.00001", "1000000000.0000", "1e2" })
        {
            input["draft"]!["entries"]![0]!["quantity"] = value;
            Assert.Equal(50400, (await Assert.ThrowsAsync<SqlException>(() => SaveV3(input.ToJsonString()))).Number);
        }
        input["draft"]!["entries"]![0]!["quantity"] = "10.0000";
        var validLine = input["draft"]!["entries"]![0]!.DeepClone();
        foreach (var (field, value) in new (string, string?)[] { ("pricingUnit", "piece"), ("unitOfMeasure", null), ("pricingUnit", "carat "), ("indicativePrice", "1.0000"), ("pricePerQuantity", "0.0000"), ("unitPrice", "1000000000000000.0000"), ("supplierSku", new string('x', 201)), ("itemType", new string('x', 101)) })
        {
            input["draft"]!["entries"]![0] = validLine.DeepClone();
            input["draft"]!["entries"]![0]![field] = value;
            Assert.Equal(50400, (await Assert.ThrowsAsync<SqlException>(() => SaveV3(input.ToJsonString()))).Number);
        }
        // AND the exact gross bound is enforced without prematurely rounding products or division.
        input["draft"]!["entries"]![0] = validLine.DeepClone();
        var line = input["draft"]!["entries"]![0]!;
        line["pricingUnit"] = "piece"; line["pricingQuantity"] = null;
        line["quantity"] = "10000.0000"; line["unitPrice"] = "999999999999999.9999";
        await SaveV3(input.ToJsonString());
        line["quantity"] = "10000.0001";
        Assert.Equal(50400, (await Assert.ThrowsAsync<SqlException>(() => SaveV3(input.ToJsonString()))).Number);
        async Task<Guid> SaveV3(string json)
        {
            await using var command = new SqlCommand("Purchasing.CreateDraftOrderV3", connection) { CommandType = System.Data.CommandType.StoredProcedure };
            command.Parameters.AddWithValue("@RequestId", Guid.NewGuid()); command.Parameters.AddWithValue("@ActorUserId", actor); command.Parameters.AddWithValue("@CanonicalInputJson", json);
            await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            return reader.GetGuid(reader.GetOrdinal("DraftOrderId"));
        }
    }
}
