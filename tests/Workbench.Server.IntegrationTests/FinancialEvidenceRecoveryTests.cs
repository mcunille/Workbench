// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Storage;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class FinancialEvidenceRecoveryTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData("available")]
    [InlineData("missing")]
    [InlineData("corrupt")]
    public async Task LegacyPurgedEvidenceIsInventoriedVerifiedAndNeverResurrected(string content)
    {
        // GIVEN BK-06 posted evidence removed before upgrade, with SQL already marking its revision purged.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer, FinancialEvidenceMigrationTests.PriorMigration);
        using var source = new PurchaseOrderDocumentEndpointTests.TestStorage();
        using var recovered = new PurchaseOrderDocumentEndpointTests.TestStorage("recovered");
        var document = await FinancialEvidencePostingTests.UploadAsync(context, source);
        await FinancialEvidenceRemovalTests.PostAsync(context, document);
        await context.AdminAsync("""
            UPDATE Purchasing.PurchaseOrderDocuments SET RemovedAtUtc=SYSUTCDATETIME();
            UPDATE Storage.Attachments SET CurrentRevisionId=NULL,DeletedAtUtc=SYSUTCDATETIME(),DeleteAfterUtc=DATEADD(day,-1,SYSUTCDATETIME());
            UPDATE Storage.Revisions SET State=3;
            """);
        var hashes = await FinancialEvidencePostingTests.FinancialHashesAsync(context);
        await DatabaseMigrator.MigrateAsync(context.Journal.Application.AdminConnectionString, default);
        Assert.True(await context.ScalarAsync<bool>("SELECT IsPending FROM Security.BlobRecoveryState"));
        // WHEN a backup manifest is exported it must include this retained identity and verify real bytes.
        var entry = Assert.Single(await StorageMaintenanceCommand.ReadEntriesAsync(context.Journal.Application.AdminConnectionString, default));
        Assert.Equal(document.Revision, entry.RevisionId);
        await BlobMaintenance.VerifyAsync(source.Store, entry, default);
        if (content == "available") await BlobMaintenance.CopyAsync(source.Store, recovered.Store, entry, default);
        if (content == "corrupt")
        {
            await recovered.Store.StageAsync(new(JournalTestContext.TenantId, document.Revision), new MemoryStream([1, 2, 3]), 3, default);
            await recovered.Store.PublishAsync(new(JournalTestContext.TenantId, document.Revision), default);
        }
        await MarkRecoveryAsync(context);
        var json = await InventoryAsync(context);
        var inventory = JsonSerializer.Deserialize<RecoveryInventory>(json)!;
        var row = Assert.Single(inventory.Rows);
        Assert.True(row.FinanciallyProtected); Assert.False(row.Disposed);
        // THEN inspection verifies protected purged bytes, preserving identity even when bytes are missing/corrupt.
        var report = await FileRecovery.InspectAsync(inventory, json, recovered.Store, Guid.NewGuid(), default);
        Assert.Empty(report.Orphans);
        if (content == "available") Assert.Empty(report.Missing);
        else Assert.Equal(content == "missing" ? "Missing" : "Corrupt", Assert.Single(report.Missing).Reason);
        await AcceptAsync(context, json, recovered.Store.Alias, JsonSerializer.Serialize(report.Missing));
        Assert.Equal(recovered.Store.Alias, await context.ScalarAsync<string>("SELECT ProviderAlias FROM Storage.Revisions"));
        Assert.Equal(hashes, await FinancialEvidencePostingTests.FinancialHashesAsync(context));
        Assert.Equal(3, await context.ScalarAsync<int>("SELECT CONVERT(int,State) FROM Storage.Revisions"));
        Assert.Equal(1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.PurchaseOrderDocuments WHERE RemovedAtUtc IS NOT NULL"));
        Assert.True(await context.ScalarAsync<bool>("SELECT Held FROM Storage.Attachments"));
    }

    [Fact]
    public async Task IndependentHoldChangeInvalidatesARecoveryReport()
    {
        // GIVEN authentic posted evidence and an inventory prepared under the isolated recovery gate.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var document = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        await FinancialEvidenceRemovalTests.PostAsync(context, document);
        await MarkRecoveryAsync(context);
        var original = await InventoryAsync(context);
        // WHEN an independent hold changes without changing the revision rowversion.
        await context.AdminAsync("UPDATE Storage.Attachments SET IndependentHeld=1,Held=1");
        // THEN the exact former report cannot authorize recovery.
        var current = await InventoryAsync(context);
        Assert.NotEqual(original, current);
        Assert.NotNull(JsonSerializer.Deserialize<RecoveryInventory>(current)!.FinancialEvidenceFingerprint);
        Assert.Equal(50043, (await Assert.ThrowsAsync<SqlException>(() => AcceptAsync(context, original, "recovered", "[]"))).Number);
    }

    [Fact]
    public async Task DisposedBackupBytesRemainDisposedAndDoNotRequireRecovery()
    {
        // GIVEN posted evidence with legitimate complete disposal authority, while an older copy still exists.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var source = new PurchaseOrderDocumentEndpointTests.TestStorage();
        using var recovered = new PurchaseOrderDocumentEndpointTests.TestStorage("recovered");
        var document = await FinancialEvidencePostingTests.UploadAsync(context, source);
        await FinancialEvidenceRemovalTests.PostAsync(context, document);
        await FinancialEvidenceDisposalTests.ExpireAsync(context);
        var entry = Assert.Single(await StorageMaintenanceCommand.ReadEntriesAsync(context.Journal.Application.AdminConnectionString, default));
        await BlobMaintenance.CopyAsync(source.Store, recovered.Store, entry, default);
        await FinancialEvidenceDisposalTests.ExecuteAsync(context, await FinancialEvidenceDisposalTests.CommandAsync(context, document.Document, Guid.NewGuid()));
        var history = await context.ScalarAsync<string>("SELECT * FROM Accounting.FinancialEvidenceDisposals FOR JSON PATH");
        await MarkRecoveryAsync(context);
        // WHEN SQL-authoritative recovery sees the old backup copy it preserves the disposition and the permanent identity.
        var json = await InventoryAsync(context);
        var inventory = JsonSerializer.Deserialize<RecoveryInventory>(json)!;
        Assert.True(Assert.Single(inventory.Rows).Disposed);
        var report = await FileRecovery.InspectAsync(inventory, json, recovered.Store, Guid.NewGuid(), default);
        Assert.Empty(report.Orphans); Assert.Empty(report.Missing);
        // AND absent disposed bytes also require no missing-file exception and are omitted from future manifests.
        await recovered.Store.DeleteAsync(new(JournalTestContext.TenantId, document.Revision), default);
        Assert.Empty((await FileRecovery.InspectAsync(inventory, json, recovered.Store, Guid.NewGuid(), default)).Missing);
        await AcceptAsync(context, json, recovered.Store.Alias, "[]");
        Assert.Empty(await StorageMaintenanceCommand.ReadEntriesAsync(context.Journal.Application.AdminConnectionString, default));
        Assert.Equal(history, await context.ScalarAsync<string>("SELECT * FROM Accounting.FinancialEvidenceDisposals FOR JSON PATH"));
        Assert.Equal(1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.PurchaseOrderDocuments WHERE RemovedAtUtc IS NOT NULL"));
    }

    internal static Task MarkRecoveryAsync(SupplierBillTestContext context) => context.AdminAsync("""
        UPDATE Security.DatabaseSecurityState SET RestoreGeneration=1,RestoreSanitizedGeneration=1;
        UPDATE Security.BlobRecoveryState SET IsPending=1;
        """);

    internal static Task<string> InventoryAsync(SupplierBillTestContext context) => context.ScalarAsync<string>(
        "DECLARE @inventory nvarchar(max); EXEC Storage.ReadRecoveryInventory @inventory OUTPUT; SELECT @inventory;");

    internal static async Task AcceptAsync(SupplierBillTestContext context, string inventory, string alias, string missing)
    {
        await using var connection = new SqlConnection(context.Journal.Application.AdminConnectionString); await connection.OpenAsync();
        await using var command = new SqlCommand("BEGIN TRAN; EXEC Storage.AcceptFileRecovery @report,1,@fingerprint,@alias,@missing; COMMIT;", connection);
        command.Parameters.AddWithValue("@report", Guid.NewGuid()); command.Parameters.AddWithValue("@fingerprint", FileRecovery.Fingerprint(inventory));
        command.Parameters.AddWithValue("@alias", alias); command.Parameters.AddWithValue("@missing", missing);
        await command.ExecuteNonQueryAsync();
    }
}
