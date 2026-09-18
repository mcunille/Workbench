// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.Persistence;
using Xunit;
namespace Workbench.Server.IntegrationTests;

public sealed partial class DraftOrderDatabaseTests
{
    [Fact]
    public async Task BetaMigrationRetiresHistoricalCommandsAndPreservesStoredData()
    {
        // GIVEN successful receipts created by each historical writer before the beta migration.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync("AddDraftSupplierOrders");
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid(); var colleague = Guid.NewGuid(); var otherTenant = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, otherTenant);
        await SeedActor(database, tenant, actor); await SeedActor(database, tenant, colleague); await SeedActor(database, otherTenant, Guid.NewGuid());
        var web = await database.CreateWebUserAsync();
        await using var connection = await Open(database, web, tenant);
        var receipts = new List<(Guid Request, string Canonical, int Format, Guid Id, byte[] Version)>();
        for (var format = 1; format <= 4; format++)
        {
            if (format == 2) await DatabaseMigrator.MigrateToAsync(database.AdminConnectionString, "AddSupplierIdentityAndPurchaseReferences", default);
            if (format == 3) await DatabaseMigrator.MigrateToAsync(database.AdminConnectionString, "AddSupplierBasedDraftPricing", default);
            var input = JsonNode.Parse(LegacyCanonical("Create", null, null, $"Format {format}"))!;
            if (format == 1)
                foreach (var field in new[] { "supplierId", "supplierContactName", "supplierEmail", "supplierPhone", "supplierWebsite", "supplierPostalAddress", "supplierOrderReference", "platform" })
                    input["draft"]!.AsObject().Remove(field);
            var canonical = input.ToJsonString(); var request = Guid.NewGuid();
            await using var save = new SqlCommand($"Purchasing.CreateDraftOrder{(format == 1 ? "" : $"V{format}")}", connection) { CommandType = CommandType.StoredProcedure };
            save.Parameters.AddWithValue("@RequestId", request); save.Parameters.AddWithValue("@ActorUserId", actor); save.Parameters.AddWithValue("@CanonicalInputJson", canonical);
            await using var reader = await save.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            receipts.Add((request, canonical, format, reader.GetGuid(reader.GetOrdinal("DraftOrderId")), (byte[])reader["SavedVersion"]));
        }
        async Task<string> Snapshot()
        {
            await using var read = new SqlCommand("SELECT (SELECT Id,TenantId,IsDeleted,Title,SupplierName,PoNumber,SupplierId,SupplierContactName,SupplierEmail,SupplierPhone,SupplierWebsite,SupplierPostalAddress,SupplierOrderReference,Platform,Currency,Notes,ContentSchemaVersion,ContentJson,CreatedAtUtc,UpdatedAtUtc,CreatedByUserId,UpdatedByUserId,RowVersion FROM Purchasing.DraftOrders ORDER BY Id FOR JSON PATH) Drafts,(SELECT * FROM Purchasing.DraftOrderRequestReceipts ORDER BY RequestId FOR JSON PATH) Receipts FOR JSON PATH,WITHOUT_ARRAY_WRAPPER", connection);
            return (string)(await read.ExecuteScalarAsync())!;
        }
        var before = await Snapshot();
        // WHEN the forward migration retires old writers THEN every retained record and receipt stays byte-for-byte intact.
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        Assert.Equal(before, await Snapshot());
        await using var state = new SqlCommand("SELECT COUNT(*) FROM Purchasing.DraftOrders WHERE State<>'Draft' OR Revision<>0 OR OrderDate IS NOT NULL", connection);
        Assert.Equal(0, await state.ExecuteScalarAsync());
        // AND the renamed beta writer cannot accept the old six-field contract and silently discard newer fields.
        Assert.Equal(50400, (await Assert.ThrowsAsync<SqlException>(() => Save(connection, actor, Guid.NewGuid(), receipts[0].Canonical, "Create"))).Number);
        Assert.Equal(before, await Snapshot());
        await using var admin = new SqlConnection(database.AdminConnectionString); await admin.OpenAsync();
        await using var commands = new SqlCommand("SELECT COUNT(*) FROM sys.procedures WHERE schema_id=SCHEMA_ID('Purchasing') AND name IN ('ReplayDraftOrderReceipt','SaveDraftOrderV2','CreateDraftOrderV2','UpdateDraftOrderV2','SaveDraftOrderV3','CreateDraftOrderV3','UpdateDraftOrderV3','SaveDraftOrderV4','CreateDraftOrderV4','UpdateDraftOrderV4')", admin);
        Assert.Equal(0, await commands.ExecuteScalarAsync());
        // AND rollback cannot make the retired writers available again.
        Assert.Equal(50020, (await Assert.ThrowsAsync<SqlException>(() => DatabaseMigrator.MigrateToAsync(database.AdminConnectionString, "AddSupplierBasedDraftPricing", default))).Number);
    }

}
