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
    public async Task BetaMigrationRetiresHistoricalWritersAndPreservesExactReceiptRecovery()
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
            var input = JsonNode.Parse(Canonical("Create", null, null, $"Format {format}"))!;
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
            await using var read = new SqlCommand("SELECT (SELECT * FROM Purchasing.DraftOrders ORDER BY Id FOR JSON PATH) Drafts,(SELECT * FROM Purchasing.DraftOrderRequestReceipts ORDER BY RequestId FOR JSON PATH) Receipts FOR JSON PATH,WITHOUT_ARRAY_WRAPPER", connection);
            return (string)(await read.ExecuteScalarAsync())!;
        }
        var before = await Snapshot();
        // WHEN the forward migration retires old writers THEN every retained record and receipt stays byte-for-byte intact.
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        Assert.Equal(before, await Snapshot());
        foreach (var receipt in receipts)
        {
            await using var replay = ReplayCommand(connection, actor, receipt.Request, "Create", null, null, receipt.Format, receipt.Canonical);
            await using (var reader = await replay.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync()); Assert.True((bool)reader["Replayed"]);
                Assert.Equal(receipt.Id, (Guid)reader["DraftOrderId"]); Assert.Equal(receipt.Version, (byte[])reader["SavedVersion"]);
            }
            // AND neither a different actor nor changed input can claim the existing request identifier.
            replay.Parameters["@ActorUserId"].Value = colleague;
            Assert.Equal(50410, (await Assert.ThrowsAsync<SqlException>(() => replay.ExecuteNonQueryAsync())).Number);
            replay.Parameters["@ActorUserId"].Value = actor; replay.Parameters["@CanonicalInputJson"].Value = receipt.Canonical + " ";
            Assert.Equal(50410, (await Assert.ThrowsAsync<SqlException>(() => replay.ExecuteNonQueryAsync())).Number);
            replay.Parameters["@CanonicalInputJson"].Value = receipt.Canonical; replay.Parameters["@FingerprintVersion"].Value = receipt.Format == 4 ? 1 : 4;
            Assert.Equal(50410, (await Assert.ThrowsAsync<SqlException>(() => replay.ExecuteNonQueryAsync())).Number);
            replay.Parameters["@FingerprintVersion"].Value = receipt.Format; replay.Parameters["@RequestId"].Value = Guid.NewGuid();
            Assert.Equal(50427, (await Assert.ThrowsAsync<SqlException>(() => replay.ExecuteNonQueryAsync())).Number);
        }
        // AND receipt recovery never changes content, numbering, versions or evidence.
        Assert.Equal(before, await Snapshot());
        // AND the renamed beta writer cannot accept the old six-field contract and silently discard newer fields.
        Assert.Equal(50400, (await Assert.ThrowsAsync<SqlException>(() => Save(connection, actor, Guid.NewGuid(), receipts[0].Canonical, "Create"))).Number);
        Assert.Equal(before, await Snapshot());
        await using var admin = new SqlConnection(database.AdminConnectionString); await admin.OpenAsync();
        await using var commands = new SqlCommand("SELECT COUNT(*) FROM sys.procedures WHERE schema_id=SCHEMA_ID('Purchasing') AND name IN ('SaveDraftOrderV2','CreateDraftOrderV2','UpdateDraftOrderV2','SaveDraftOrderV3','CreateDraftOrderV3','UpdateDraftOrderV3','SaveDraftOrderV4','CreateDraftOrderV4','UpdateDraftOrderV4')", admin);
        Assert.Equal(0, await commands.ExecuteScalarAsync());
        // AND rollback cannot make the retired writers available again.
        Assert.Equal(50020, (await Assert.ThrowsAsync<SqlException>(() => DatabaseMigrator.MigrateToAsync(database.AdminConnectionString, "AddSupplierBasedDraftPricing", default))).Number);
    }

    [Fact]
    public async Task BetaReceiptLookupRequiresAuthorityAndCannotCreateMissingWork()
    {
        // GIVEN a fresh beta database and a restricted runtime principal.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        var web = await database.CreateWebUserAsync(); await using var connection = new SqlConnection(web); await connection.OpenAsync();
        await using var replay = ReplayCommand(connection, actor, Guid.NewGuid(), "Create", null, null, 4, Canonical("Create", null, null, "Missing"));
        // WHEN no tenant proof exists THEN lookup is forbidden.
        Assert.Equal(50403, (await Assert.ThrowsAsync<SqlException>(() => replay.ExecuteNonQueryAsync())).Number);
        await using var authorized = await Open(database, web, tenant); replay.Connection = authorized;
        // WHEN an authorized caller supplies a new request THEN it is unsupported and cannot create a draft or receipt.
        Assert.Equal(50427, (await Assert.ThrowsAsync<SqlException>(() => replay.ExecuteNonQueryAsync())).Number);
        await using var count = new SqlCommand("SELECT (SELECT COUNT(*) FROM Purchasing.DraftOrders)+(SELECT COUNT(*) FROM Purchasing.DraftOrderRequestReceipts)", authorized);
        Assert.Equal(0, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task BetaReceiptRecoveryChecksOperationTargetVersionTenantAndDisabledActorAfterDeletion()
    {
        // GIVEN a draft with create, update and delete receipts, followed by its retained tombstone.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid(); var foreignTenant = Guid.NewGuid(); var foreignActor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, foreignTenant); await SeedActor(database, tenant, actor); await SeedActor(database, foreignTenant, foreignActor);
        var web = await database.CreateWebUserAsync(); await using var connection = await Open(database, web, tenant);
        var created = await Save(connection, actor, Guid.NewGuid(), Canonical("Create", null, null, "Original"), "Create");
        var request = Guid.NewGuid(); var canonical = Canonical("Update", created.Id, created.Version, "Updated");
        var updated = await Save(connection, actor, request, canonical, "Update");
        var deleteRequest = Guid.NewGuid(); var deleted = await Delete(connection, actor, deleteRequest, created.Id, updated.Version);
        await using var replay = ReplayCommand(connection, actor, request, "Update", created.Id, created.Version, 4, canonical);
        // WHEN the exact update retries after deletion THEN it returns its original version without reviving content.
        await using (var receipt = await replay.ExecuteReaderAsync())
        {
            Assert.True(await receipt.ReadAsync()); Assert.Equal(updated.Version, (byte[])receipt["SavedVersion"]);
        }
        // AND operation, target and expected-version mismatches independently conflict even with the exact fingerprint.
        foreach (var (parameter, wrong) in new (string, object)[] { ("@Operation", "Delete"), ("@DraftOrderId", Guid.NewGuid()), ("@ExpectedRowVersion", updated.Version) })
        {
            var original = replay.Parameters[parameter].Value; replay.Parameters[parameter].Value = wrong;
            Assert.Equal(50410, (await Assert.ThrowsAsync<SqlException>(() => replay.ExecuteNonQueryAsync())).Number);
            replay.Parameters[parameter].Value = original;
        }
        // AND deletion uses the historical, literal base64 envelope without JSON encoder transformations.
        var deletionCanonical = "{\"operation\":\"Delete\",\"targetId\":\"" + created.Id.ToString("D")
            + "\",\"expectedVersion\":\"" + Convert.ToBase64String(updated.Version) + "\",\"draft\":null}";
        await using var deleteReplay = ReplayCommand(connection, actor, deleteRequest, "Delete", created.Id, updated.Version, 1, deletionCanonical);
        await using (var receipt = await deleteReplay.ExecuteReaderAsync())
        {
            Assert.True(await receipt.ReadAsync()); Assert.Equal(deleted.Version, (byte[])receipt["SavedVersion"]);
        }
        // AND another tenant cannot discover the receipt, while a disabled actor cannot recover it.
        await using var foreign = await Open(database, web, foreignTenant);
        replay.Connection = foreign; replay.Parameters["@ActorUserId"].Value = foreignActor;
        Assert.Equal(50427, (await Assert.ThrowsAsync<SqlException>(() => replay.ExecuteNonQueryAsync())).Number);
        replay.Connection = connection; replay.Parameters["@ActorUserId"].Value = actor;
        await using var admin = new SqlConnection(database.AdminConnectionString); await admin.OpenAsync();
        await using var disable = new SqlCommand("UPDATE [Identity].[Users] SET State=2 WHERE Id=@id", admin); disable.Parameters.AddWithValue("@id", actor); await disable.ExecuteNonQueryAsync();
        Assert.Equal(50403, (await Assert.ThrowsAsync<SqlException>(() => replay.ExecuteNonQueryAsync())).Number);
        await using var read = new SqlCommand("SELECT IsDeleted FROM Purchasing.DraftOrders WHERE Id=@id", connection); read.Parameters.AddWithValue("@id", created.Id);
        Assert.True((bool)(await read.ExecuteScalarAsync())!);
    }

    private static SqlCommand ReplayCommand(SqlConnection connection, Guid actor, Guid request, string operation, Guid? target, byte[]? version, int format, string canonical)
    {
        var command = new SqlCommand("Purchasing.ReplayDraftOrderReceipt", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@RequestId", request); command.Parameters.AddWithValue("@ActorUserId", actor);
        command.Parameters.AddWithValue("@Operation", operation); command.Parameters.Add("@DraftOrderId", SqlDbType.UniqueIdentifier).Value = (object?)target ?? DBNull.Value;
        command.Parameters.Add("@ExpectedRowVersion", SqlDbType.VarBinary, -1).Value = (object?)version ?? DBNull.Value;
        command.Parameters.AddWithValue("@FingerprintVersion", format); command.Parameters.AddWithValue("@CanonicalInputJson", canonical);
        return command;
    }
}
