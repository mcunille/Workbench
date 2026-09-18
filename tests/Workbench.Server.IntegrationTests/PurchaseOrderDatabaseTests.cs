// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.Purchasing;
using Xunit;
namespace Workbench.Server.IntegrationTests;

public sealed partial class DraftOrderDatabaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PurchaseHistoryIsAtomicImmutableTenantScopedAndUpgradesRetainedDrafts(bool upgrade)
    {
        // GIVEN a saved financial draft and its exact receipt in the supported predecessor or fresh schema.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync(upgrade ? "IntegrateBetaDraftFinancialAdjustments" : null);
        var tenant = Guid.NewGuid(); var foreign = Guid.NewGuid(); var actor = Guid.NewGuid(); var request = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, foreign); await SeedActor(database, tenant, actor);
        var web = await database.CreateWebUserAsync();
        await using var connection = await Open(database, web, tenant);
        var supplier = await SaveSupplier(connection, actor, Guid.NewGuid(), SupplierCanonical("Create", null, null, new("Supplier Alpha", null, null, null, null, null)));
        var draft = DraftOrderInput.Normalize(DraftOrderPricingTests.Empty with { SupplierId = supplier.Id, SupplierName = "Supplier Alpha", Entries = [DraftOrderPricingTests.Line with { Description = "Sapphire" }] });
        var original = JsonNode.Parse(DraftOrderInput.Canonical("Create", null, null, draft))!;
        var saved = await SaveFinancial(connection, actor, request, original);
        if (upgrade) await Workbench.Server.Persistence.DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        // WHEN committing THEN the authoritative calculation and original identifiers are persisted atomically.
        var commitRequest = Guid.NewGuid();
        var committed = await Purchase(connection, actor, saved.Id, commitRequest, saved.Version, "Commit", null);
        var replay = await SaveFinancial(connection, actor, request, original);
        Assert.True(replay.Replayed); Assert.Equal(saved.Version, replay.Version);
        await using var read = new SqlCommand("SELECT CalculationJson FROM Purchasing.PurchaseOrderRevisions WHERE DraftOrderId=@id AND Revision=1", connection);
        read.Parameters.AddWithValue("@id", saved.Id);
        var snapshot = (string)(await read.ExecuteScalarAsync())!;
        Assert.Equal(DraftOrderInput.Calculate(draft), JsonSerializer.Deserialize<DraftCalculationResponse>(snapshot, DraftOrderInput.JsonOptions)!, new CalculationComparer());
        // AND current draft writers cannot overwrite or delete the ordered projection.
        var update = JsonNode.Parse(DraftOrderInput.Canonical("Update", saved.Id, Convert.ToBase64String(committed.Version), draft with { Title = "Bypass" }))!;
        Assert.Equal(50415, (await Assert.ThrowsAsync<SqlException>(() => SaveFinancial(connection, actor, Guid.NewGuid(), update))).Number);
        await using var deletion = new SqlCommand("Purchasing.DeleteDraftOrder", connection) { CommandType = CommandType.StoredProcedure };
        deletion.Parameters.AddWithValue("@RequestId", Guid.NewGuid()); deletion.Parameters.AddWithValue("@ActorUserId", actor); deletion.Parameters.AddWithValue("@DraftOrderId", saved.Id); deletion.Parameters.AddWithValue("@ExpectedRowVersion", committed.Version);
        Assert.Equal(50415, (await Assert.ThrowsAsync<SqlException>(() => deletion.ExecuteNonQueryAsync())).Number);
        // AND the restricted principal can neither mutate evidence nor call the internal validator.
        foreach (var sql in new[] { "UPDATE Purchasing.PurchaseOrderRevisions SET Reason='changed'", "DELETE Purchasing.PurchaseOrderReceipts", "EXEC Purchasing.ValidatePurchaseOrderContent NULL,NULL,NULL" })
        { await using var denied = new SqlCommand(sql, connection); Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => denied.ExecuteNonQueryAsync())).Number); }
        // WHEN amending THEN financial validation, no-op detection and immutable currency remain authoritative in SQL.
        Assert.Equal(50417, (await Assert.ThrowsAsync<SqlException>(() => Purchase(connection, actor, saved.Id, Guid.NewGuid(), committed.Version, "Amend", draft))).Number);
        Assert.Equal(50416, (await Assert.ThrowsAsync<SqlException>(() => Purchase(connection, actor, saved.Id, Guid.NewGuid(), committed.Version, "Amend", draft with { Currency = "EUR" }))).Number);
        Assert.Equal(50400, (await Assert.ThrowsAsync<SqlException>(() => Purchase(connection, actor, saved.Id, Guid.NewGuid(), committed.Version, "Amend", draft with { OrderDiscount = new("fixed", "99999.0000") }))).Number);
        var changed = draft with { Notes = "Supplier correction" };
        var amendmentRequest = Guid.NewGuid();
        var amended = await Purchase(connection, actor, saved.Id, amendmentRequest, committed.Version, "Amend", changed);
        Assert.Equal(2, amended.Revision);
        Assert.Equal(snapshot, await read.ExecuteScalarAsync());
        await SaveSupplier(connection, actor, Guid.NewGuid(), SupplierCanonical("Update", supplier.Id, supplier.Version, new("Directory renamed supplier", null, null, null, null, null)));
        Assert.Equal(snapshot, await read.ExecuteScalarAsync());
        Assert.True((await Purchase(connection, actor, saved.Id, commitRequest, saved.Version, "Commit", null)).Replayed);
        Assert.True((await Purchase(connection, actor, saved.Id, amendmentRequest, committed.Version, "Amend", changed)).Replayed);
        Assert.Equal(50410, (await Assert.ThrowsAsync<SqlException>(() => Purchase(connection, actor, saved.Id, amendmentRequest, committed.Version, "Amend", changed with { Notes = "Different" }))).Number);
        // AND a foreign tenant cannot observe or mutate either history or its order.
        var foreignActor = Guid.NewGuid();
        await SeedActor(database, foreign, foreignActor);
        await using var other = await Open(database, web, foreign);
        await using var hidden = new SqlCommand("SELECT COUNT(*) FROM Purchasing.PurchaseOrderRevisions", other);
        Assert.Equal(0, await hidden.ExecuteScalarAsync());
        Assert.Equal(50404, (await Assert.ThrowsAsync<SqlException>(() => Purchase(other, foreignActor, saved.Id, Guid.NewGuid(), amended.Version, "Amend", changed))).Number);
    }
    [Fact]
    public async Task CommitmentUsesTheCompleteFinancialPolicyAndRejectsIncompleteLinesAtSqlBoundary()
    {
        // GIVEN restricted SQL access, independently of HTTP validation.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var basis = DraftOrderInput.Normalize(DraftOrderPricingTests.Empty with { SupplierName = "Supplier", Entries = [DraftOrderPricingTests.Line with { Description = "Stone" }] });
        // WHEN incomplete but valid drafts are committed THEN SQL rejects their missing required commitment fields.
        foreach (var invalid in new[] { basis with { SupplierName = null }, basis with { Entries = [] }, basis with { Entries = [basis.Entries[0] with { Description = null }] },
            basis with { Entries = [basis.Entries[0] with { Quantity = null, PriceMode = "lineTotal" }] }, basis with { Entries = [basis.Entries[0] with { UnitOfMeasure = null }] },
            basis with { Currency = null, Entries = [basis.Entries[0] with { Price = null }] }, basis with { Entries = [basis.Entries[0] with { Price = null, IndicativePrice = "1.0000" }] } })
        {
            var row = await SaveFinancial(connection, actor, Guid.NewGuid(), JsonNode.Parse(DraftOrderInput.Canonical("Create", null, null, invalid))!);
            Assert.Equal(50400, (await Assert.ThrowsAsync<SqlException>(() => Purchase(connection, actor, row.Id, Guid.NewGuid(), row.Version, "Commit", null))).Number);
        }
        // AND authoritative historical calculations agree with the pure policy for discounts, precision, unknowns and independent payees.
        var charge = new DraftCharge(Guid.NewGuid(), "shipping", "Freight", "2.0000", "supplier", null, "estimated", null, null);
        foreach (var candidate in new[] {
            basis with { OrderDiscount = new("percentage", "10.0000"), Entries = [basis.Entries[0] with { Discount = new("percentage", "12.3456") }], Charges = [charge, charge with { Id = Guid.NewGuid(), PayeeKind = "thirdParty", PayeeName = "Broker", Amount = "3.5000", AmountStatus = "confirmed" }] },
            basis with { OrderDiscount = new("fixed", "10.0000"), Entries = [basis.Entries[0] with { Discount = new("fixed", "1.0000") }] },
            basis with { Entries = [basis.Entries[0] with { Price = null, Discount = new("fixed", "1.0000") }], OrderDiscount = new("fixed", "2.0000"), Charges = [charge with { Amount = null }] },
            basis with { Entries = [basis.Entries[0], basis.Entries[0] with { Id = Guid.NewGuid(), Price = null }], Charges = [charge with { Amount = null, PayeeKind = "thirdParty", PayeeName = "Broker" }] },
            basis with { Entries = [basis.Entries[0] with { Quantity = "1.0001", Price = "0.5000" }] },
            basis with { Entries = [basis.Entries[0] with { PriceMode = "lineTotal", Price = "9999999999999999999.0000" }] }
        })
        {
            var row = await SaveFinancial(connection, actor, Guid.NewGuid(), JsonNode.Parse(DraftOrderInput.Canonical("Create", null, null, candidate))!);
            await Purchase(connection, actor, row.Id, Guid.NewGuid(), row.Version, "Commit", null);
            await using var read = new SqlCommand("SELECT CalculationJson FROM Purchasing.PurchaseOrderRevisions WHERE DraftOrderId=@id", connection);
            read.Parameters.AddWithValue("@id", row.Id);
            var actual = JsonSerializer.Deserialize<DraftCalculationResponse>((string)(await read.ExecuteScalarAsync())!, DraftOrderInput.JsonOptions)!;
            Assert.Equal(DraftOrderInput.Calculate(candidate), actual, new CalculationComparer());
        }
    }
    private sealed class CalculationComparer : IEqualityComparer<DraftCalculationResponse>
    {
        public bool Equals(DraftCalculationResponse? x, DraftCalculationResponse? y) => JsonSerializer.Serialize(x, DraftOrderInput.JsonOptions) == JsonSerializer.Serialize(y, DraftOrderInput.JsonOptions);
        public int GetHashCode(DraftCalculationResponse obj) => 0;
    }
    private static async Task<(byte[] Version, int Revision, bool Replayed)> Purchase(SqlConnection connection, Guid actor, Guid id, Guid request, byte[] version, string operation, DraftContent? draft, string orderDate = "2026-09-11", string? rawDraft = null)
    {
        await using var command = new SqlCommand("Purchasing.SavePurchaseOrder", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@RequestId", request); command.Parameters.AddWithValue("@ActorUserId", actor); command.Parameters.AddWithValue("@TargetId", id);
        command.Parameters.AddWithValue("@ExpectedVersion", version); command.Parameters.AddWithValue("@Operation", operation); command.Parameters.AddWithValue("@OrderDate", orderDate);
        command.Parameters.Add(new("@Reason", SqlDbType.NVarChar, -1) { Value = operation == "Amend" ? "Supplier correction" : DBNull.Value });
        command.Parameters.Add(new("@Draft", SqlDbType.NVarChar, -1) { Value = rawDraft ?? (object?)(draft is null ? null : JsonSerializer.Serialize(draft, DraftOrderInput.JsonOptions)) ?? DBNull.Value });
        // A caller-supplied calculation must never forge historical financial evidence.
        command.Parameters.AddWithValue("@Calculation", "{}");
        await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        return ((byte[])reader["SavedVersion"], reader.GetInt32(reader.GetOrdinal("Revision")), reader.GetBoolean(reader.GetOrdinal("Replayed")));
    }
}
