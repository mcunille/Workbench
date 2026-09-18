// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Text.Json.Nodes;
using Workbench.Server.Purchasing;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Tenancy;
using Xunit;
namespace Workbench.Server.IntegrationTests;

public sealed partial class DraftOrderDatabaseTests
{
    [Fact]
    public async Task PurchaseDocumentReservationsBoundCapacityAndTenantScopedRequestsAreImmutable()
    {
        // GIVEN an ordered purchase and independently open restricted SQL connections.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var other = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, other); await SeedActor(database, tenant, actor);
        var connectionString = await database.CreateWebUserAsync();
        var proof = new TenantContextProof(await database.GetTenantContextProofKeyAsync());
        await using var first = new SqlConnection(connectionString); await first.OpenAsync(); await proof.ApplyAsync(first, tenant, default);
        await using var second = new SqlConnection(connectionString); await second.OpenAsync(); await proof.ApplyAsync(second, tenant, default);
        var draft = DraftOrderInput.Normalize(DraftOrderPricingTests.Empty with { SupplierName = "Supplier", Entries = [DraftOrderPricingTests.Line with { Description = "Sapphire" }] });
        var savedOrder = await SaveFinancial(first, actor, Guid.NewGuid(), JsonNode.Parse(DraftOrderInput.Canonical("Create", null, null, draft))!);
        var committed = await Purchase(first, actor, savedOrder.Id, Guid.NewGuid(), savedOrder.Version, "Commit", null);
        var order = savedOrder.Id; var orderVersion = committed.Version;
        await using var admin = new SqlConnection(database.AdminConnectionString); await admin.OpenAsync();
        // WHEN malformed labels and noncanonical content types reach restricted SQL directly THEN they are rejected before reservation.
        foreach (var label in new[] { "\t\n\u2003", new string('x', 201) })
            Assert.Equal(50076, (await Assert.ThrowsAsync<SqlException>(() => PrepareDocument(first, order, actor, Guid.NewGuid(), orderVersion, label))).Number);
        foreach (var mediaType in new[] { "APPLICATION/PDF", "application/pdf ", "application/pdf" + new string(' ', 200) })
            Assert.Equal(50076, (await Assert.ThrowsAsync<SqlException>(() => PrepareDocument(first, order, actor, Guid.NewGuid(), orderVersion, mediaType: mediaType))).Number);
        var request = Guid.NewGuid();
        // WHEN retrying identical evidence THEN only one pending reservation exists.
        await PrepareDocument(first, order, actor, request, orderVersion);
        await PrepareDocument(second, order, actor, request, orderVersion);
        // AND reuse with a changed label conflicts rather than replacing immutable evidence.
        Assert.Equal(50077, (await Assert.ThrowsAsync<SqlException>(() => PrepareDocument(second, order, actor, request, orderVersion, "Different"))).Number);
        for (var n = 1; n < 19; n++) await PrepareDocument(first, order, actor, Guid.NewGuid(), orderVersion);
        // WHEN independent connections compete for the last slot THEN precisely one reservation wins.
        async Task<bool> Attempt(SqlConnection connection)
        {
            try { await PrepareDocument(connection, order, actor, Guid.NewGuid(), orderVersion); return true; }
            catch (SqlException error) when (error.Number == 50079) { return false; }
        }
        var winners = await Task.WhenAll(Attempt(first), Attempt(second));
        Assert.Single(winners, value => value);
        await using var count = new SqlCommand("SELECT COUNT(*) FROM Purchasing.PurchaseOrderDocumentOperations", first);
        Assert.Equal(20, await count.ExecuteScalarAsync());
        await using var visible = new SqlCommand("SELECT COUNT(*) FROM Purchasing.PurchaseOrderDocuments", first);
        Assert.Equal(0, await visible.ExecuteScalarAsync());
        // WHEN publication finalizes the first reservation THEN it becomes visible and advances the parent version without an agreed-content revision.
        await FinishDocument(first, request);
        await using var saved = new SqlCommand("SELECT COUNT(*) FROM Purchasing.PurchaseOrderDocuments WHERE RemovedAtUtc IS NULL", first);
        Assert.Equal(1, await saved.ExecuteScalarAsync());
        await using var result = new SqlCommand("SELECT State,ResultOrderVersion FROM Purchasing.PurchaseOrderDocumentOperations WHERE RequestId=@request", first);
        result.Parameters.AddWithValue("@request", request);
        await using (var reader = await result.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync()); Assert.Equal(1, reader.GetInt32(0));
            Assert.NotEqual(orderVersion, (byte[])reader[1]);
        }
        // WHEN another already-published reservation loses that version race THEN its bytes enter retention and capacity is released.
        await using var pending = new SqlCommand("SELECT TOP(1) RequestId FROM Purchasing.PurchaseOrderDocumentOperations WHERE State=0", first);
        var stale = (Guid)(await pending.ExecuteScalarAsync())!;
        await FinishDocument(second, stale);
        await using var conflict = new SqlCommand("SELECT State FROM Purchasing.PurchaseOrderDocumentOperations WHERE RequestId=@request", first);
        conflict.Parameters.AddWithValue("@request", stale); Assert.Equal(2, await conflict.ExecuteScalarAsync());
        await using var retention = new SqlCommand("SELECT COUNT(*) FROM Operations.WorkItems WHERE Kind=1", first);
        Assert.Equal(1, await retention.ExecuteScalarAsync());
        // WHEN an operator definitively reconciles an unpublished reservation as failed THEN capacity releases and replay evidence remains.
        await using var reconciled = new SqlCommand("UPDATE Storage.Revisions SET State=2 WHERE Id=(SELECT TOP(1) RevisionId FROM Purchasing.PurchaseOrderDocumentOperations WHERE State=0)", admin);
        await reconciled.ExecuteNonQueryAsync();
        await using var failures = new SqlCommand("SELECT COUNT(*) FROM Purchasing.PurchaseOrderDocumentOperations WHERE State=2", first);
        Assert.Equal(2, await failures.ExecuteScalarAsync());
        // AND the narrow maintenance role cannot grant itself direct mutation of document command evidence.
        await using (var maintenance = new SqlConnection(await database.CreateRoleUserAsync("workbench_storage_maintenance")))
        {
            await maintenance.OpenAsync();
            await using var denied = new SqlCommand("UPDATE Purchasing.PurchaseOrderDocumentOperations SET State=2", maintenance);
            Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => denied.ExecuteNonQueryAsync())).Number);
        }
        // AND committed replay remains available after later document changes without another retention job.
        await PrepareDocument(first, order, actor, request, orderVersion);
        await FinishDocument(first, request);
        Assert.Equal(1, await saved.ExecuteScalarAsync()); Assert.Equal(1, await retention.ExecuteScalarAsync());
        await using var revision = new SqlCommand("SELECT Revision FROM Purchasing.DraftOrders WHERE Id=@id", first);
        revision.Parameters.AddWithValue("@id", order); Assert.Equal(1, await revision.ExecuteScalarAsync());
        // AND a foreign tenant sees no metadata and cannot use the item relationship.
        await using var foreign = new SqlConnection(connectionString);
        await foreign.OpenAsync();
        await proof.ApplyAsync(foreign, other, default);
        var otherActor = Guid.NewGuid(); await SeedActor(database, other, otherActor);
        await using var hidden = new SqlCommand("SELECT COUNT(*) FROM Purchasing.PurchaseOrderDocumentOperations", foreign);
        Assert.Equal(0, await hidden.ExecuteScalarAsync());
        Assert.Equal(50078, (await Assert.ThrowsAsync<SqlException>(() => PrepareDocument(foreign, order, otherActor, Guid.NewGuid(), orderVersion))).Number);
    }
    [Fact]
    public async Task PurchaseDocumentUpgradePreservesOrderedSnapshotAndRejectsRollback()
    {
        // GIVEN the immediate predecessor with an ordered purchase and immutable financial history.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync("AddPurchaseOrderCommitment");
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        var web = await database.CreateWebUserAsync();
        await using var connection = await Open(database, web, tenant);
        var draft = DraftOrderInput.Normalize(DraftOrderPricingTests.Empty with { SupplierName = "Supplier", Entries = [DraftOrderPricingTests.Line with { Description = "Sapphire" }] });
        var saved = await SaveFinancial(connection, actor, Guid.NewGuid(), JsonNode.Parse(DraftOrderInput.Canonical("Create", null, null, draft))!);
        var committed = await Purchase(connection, actor, saved.Id, Guid.NewGuid(), saved.Version, "Commit", null);
        await using var read = new SqlCommand("SELECT (SELECT * FROM Purchasing.DraftOrders FOR JSON PATH) Orders,(SELECT * FROM Purchasing.PurchaseOrderRevisions FOR JSON PATH) Revisions,(SELECT * FROM Purchasing.PurchaseOrderReceipts FOR JSON PATH) Receipts FOR JSON PATH", connection);
        var before = (string)(await read.ExecuteScalarAsync())!;
        // WHEN upgrading THEN financial history, parent versions and command receipts remain byte-for-byte unchanged.
        await Workbench.Server.Persistence.DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        Assert.Equal(before, await read.ExecuteScalarAsync());
        await PrepareDocument(connection, saved.Id, actor, Guid.NewGuid(), committed.Version);
        // AND destructive rollback cannot remove pending paperwork or replay evidence.
        Assert.Equal(50020, (await Assert.ThrowsAsync<SqlException>(() => Workbench.Server.Persistence.DatabaseMigrator.MigrateToAsync(database.AdminConnectionString, "AddPurchaseOrderCommitment", default))).Number);
        await using var evidence = new SqlCommand("SELECT COUNT(*) FROM Purchasing.PurchaseOrderDocumentOperations", connection);
        Assert.Equal(1, await evidence.ExecuteScalarAsync());
    }
    private static async Task FinishDocument(SqlConnection connection, Guid request)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = new SqlCommand("Purchasing.FinishPurchaseOrderDocument", connection, transaction) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@RequestId", request); command.Parameters.AddWithValue("@Published", true);
        await command.ExecuteNonQueryAsync(); await transaction.CommitAsync();
    }
    private static async Task PrepareDocument(SqlConnection connection, Guid order, Guid actor, Guid request, byte[] orderVersion, string label = "Receipt", string mediaType = "application/pdf")
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = new SqlCommand("Purchasing.PreparePurchaseOrderDocument", connection, transaction) { CommandType = CommandType.StoredProcedure };
        foreach (var (name, value) in new (string, object)[] { ("@OrderId", order), ("@RequestId", request), ("@ExpectedOrderVersion", orderVersion), ("@Kind", 0), ("@Label", label), ("@MediaType", mediaType), ("@Extension", "pdf"), ("@Length", 12L), ("@Sha256", new string('A', 64)), ("@ProviderAlias", "test"), ("@ActorUserId", actor) }) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(); await transaction.CommitAsync();
    }
}
