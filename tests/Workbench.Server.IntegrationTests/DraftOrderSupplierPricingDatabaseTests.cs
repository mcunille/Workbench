// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.Purchasing;
using Xunit;
namespace Workbench.Server.IntegrationTests;

public sealed partial class DraftOrderDatabaseTests
{
    [Fact]
    public async Task ConsolidatedMigrationPreservesRetainedPreviewHistoryAndGuardsConvertedDraft()
    {
        // GIVEN the final PO-03 schema with the earlier two-migration preview history and a V3 receipt.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        await using var admin = new SqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        await using var history = new SqlCommand("INSERT INTO dbo.__EFMigrationsHistory(MigrationId,ProductVersion) VALUES(N'20260916183834_AddStructuredDraftOrderLines',N'10.0.0')", admin);
        await history.ExecuteNonQueryAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid(); var request = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var original = Canonical("Create", null, null, "Retained order");
        var saved = await Save(connection, actor, request, original, "Create");
        // WHEN the consolidated migrator runs THEN retained history and the old receipt stay intact.
        await Workbench.Server.Persistence.DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        history.CommandText = "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory";
        Assert.Equal(19, Convert.ToInt32(await history.ExecuteScalarAsync()));
        var inspection = await Workbench.Server.Administration.DevelopmentDatabaseInspection.InspectAsync(database.AdminConnectionString, default);
        Assert.True(inspection.MigrationHistoryCompatible);
        Assert.True(inspection.SchemaCurrent);
        Assert.Equal(19, inspection.AppliedMigrations.Length);
        var replay = await Save(connection, actor, request, original, "Create");
        Assert.Equal(saved.Version, replay.Version); Assert.True(replay.Replayed);
        // AND a V4 update retains the order identity while older clients cannot overwrite the new content.
        await using var update = new SqlCommand("Purchasing.UpdateDraftOrderV4", connection) { CommandType = CommandType.StoredProcedure };
        update.Parameters.AddWithValue("@RequestId", Guid.NewGuid()); update.Parameters.AddWithValue("@ActorUserId", actor);
        update.Parameters.AddWithValue("@CanonicalInputJson", DraftOrderInputV4.Canonical("Update", saved.Id, Convert.ToBase64String(saved.Version), DraftOrderInputV4.Normalize(DraftOrderInputV4Tests.Empty with { Entries = [DraftOrderInputV4Tests.Line] })));
        byte[] current;
        await using (var reader = await update.ExecuteReaderAsync()) { Assert.True(await reader.ReadAsync()); current = (byte[])reader["SavedVersion"]; Assert.Equal(saved.Id, reader.GetGuid(reader.GetOrdinal("DraftOrderId"))); }
        Assert.Equal(50426, (await Assert.ThrowsAsync<SqlException>(() => Save(connection, actor, Guid.NewGuid(), Canonical("Update", saved.Id, current, "Old overwrite"), "Update"))).Number);
        Assert.True((await Save(connection, actor, request, original, "Create")).Replayed);
        // AND a changed currency cannot reinterpret an existing amount, even if the new request clears it.
        update.Parameters["@RequestId"].Value = Guid.NewGuid();
        update.Parameters["@CanonicalInputJson"].Value = DraftOrderInputV4.Canonical("Update", saved.Id, Convert.ToBase64String(current), DraftOrderInputV4Tests.Empty with { Currency = "EUR" });
        Assert.Equal(50401, (await Assert.ThrowsAsync<SqlException>(() => update.ExecuteNonQueryAsync())).Number);
    }
    [Fact]
    public async Task SupplierPricingCommandPreservesTotalsReceiptsAndRejectsInvalidLegacyQuotes()
    {
        // GIVEN an authorized restricted writer with the forward supplier pricing migration.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var draft = DraftOrderInputV4.Normalize(DraftOrderInputV4Tests.Empty with { Entries = [DraftOrderInputV4Tests.Line with { PriceMode = "lineTotal", Quantity = null, UnitOfMeasure = null, Price = "9999999999999999999.9999" }] });
        var request = Guid.NewGuid();
        var canonical = DraftOrderInputV4.Canonical("Create", null, null, draft);
        // WHEN a standalone maximum total is saved THEN the schema and immutable receipt use the current version.
        var id = await SaveV4(request, canonical);
        Assert.Equal(id, await SaveV4(request, canonical));
        await using var read = new SqlCommand("SELECT ContentSchemaVersion FROM Purchasing.DraftOrders WHERE Id=@id", connection);
        read.Parameters.AddWithValue("@id", id); Assert.Equal(3, Convert.ToInt32(await read.ExecuteScalarAsync()));
        // AND direct SQL callers cannot bypass retained quote validation.
        foreach (var legacy in new[] {
            new DraftLegacyPricingV4("1.0000", "piece", "1.0000", "piece", "1.0000", "1.0000"),
            new DraftLegacyPricingV4("1.0000", null, "1.0000", "carat", "1.0000", "1.0000"),
            new DraftLegacyPricingV4("999999999.0000", "piece", "999999999999999.0000", "piece", "0.0001", null)
        })
        {
            var invalid = draft with { Entries = [draft.Entries[0] with { Price = null, LegacyPricing = legacy }] };
            Assert.Equal(50400, (await Assert.ThrowsAsync<SqlException>(() => SaveV4(Guid.NewGuid(), DraftOrderInputV4.Canonical("Create", null, null, invalid)))).Number);
        }
        // AND invalid amounts, modes and simultaneous reference prices are rejected at the restricted boundary.
        foreach (var line in new[] { draft.Entries[0] with { PriceMode = "other" }, draft.Entries[0] with { Price = "-1.0000" }, draft.Entries[0] with { IndicativePrice = "1.0000" } })
            Assert.Equal(50400, (await Assert.ThrowsAsync<SqlException>(() => SaveV4(Guid.NewGuid(), DraftOrderInputV4.Canonical("Create", null, null, draft with { Entries = [line] })))).Number);
        async Task<Guid> SaveV4(Guid requestId, string json)
        {
            await using var command = new SqlCommand("Purchasing.CreateDraftOrderV4", connection) { CommandType = CommandType.StoredProcedure };
            command.Parameters.AddWithValue("@RequestId", requestId); command.Parameters.AddWithValue("@ActorUserId", actor); command.Parameters.AddWithValue("@CanonicalInputJson", json);
            await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            return reader.GetGuid(reader.GetOrdinal("DraftOrderId"));
        }
    }
}
