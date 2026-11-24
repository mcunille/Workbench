// Copyright (c) 2026 The White Stag Collection.
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Storage;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCreditRecoveryTests(SupplierCreditRecoveryFixture fixture) : IClassFixture<SupplierCreditRecoveryFixture>
{
    [Theory]
    [InlineData("available", "Available")]
    [InlineData("missing", "Missing")]
    [InlineData("corrupt", "Corrupt")]
    public async Task RecoveryPreservesCreditRefundRepaymentCorrectionChain(string content, string availability)
    {
        // GIVEN a real SQL restore of genuine held credit, refund, repayment, and correction records.
        var restored = await fixture.OpenAsync();
        await using var context = restored.Context;
        using var recovered = new PurchaseOrderDocumentEndpointTests.TestStorage("recovered");
        var bills = context.Bills;
        var before = await RetainedSources(bills);
        var id = fixture.Document.Revision;
        var entry = Assert.Single(await StorageMaintenanceCommand.ReadEntriesAsync(bills.Journal.Application.AdminConnectionString, default));
        Assert.Equal(id, entry.RevisionId);
        await BlobMaintenance.VerifyAsync(fixture.Source.Store, entry, default);
        if (content == "available") await BlobMaintenance.CopyAsync(fixture.Source.Store, recovered.Store, entry, default);
        if (content == "corrupt")
        {
            await recovered.Store.StageAsync(new(JournalTestContext.TenantId, id), new MemoryStream([1, 2, 3]), 3, default);
            await recovered.Store.PublishAsync(new(JournalTestContext.TenantId, id), default);
        }

        // WHEN SQL sanitation invalidates sessions and the matching file inventory is independently verified.
        await bills.AdminAsync("EXEC Administration.MarkRestorePending;");
        Assert.True(await bills.ScalarAsync<bool>("SELECT IsPending FROM Security.WorkbenchRestorePending"));
        await bills.AdminAsync("""
            DECLARE @now datetimeoffset=SYSUTCDATETIME();
            EXEC Administration.SanitizeRestore @Now=@now,@CorrelationId=N'bk08-paired-recovery';
            """);
        Assert.False(await bills.ScalarAsync<bool>("SELECT IsPending FROM Security.WorkbenchRestorePending"));
        Assert.True(await bills.ScalarAsync<bool>("SELECT IsPending FROM Security.BlobRecoveryState"));
        Assert.Equal(0, await bills.ScalarAsync<int>("SELECT COUNT(*) FROM [Identity].Sessions"));
        var denied = await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync("RefundRepayment",
            JsonNode.Parse(fixture.CorrectionInput)!.AsObject(), fixture.CorrectionRequest));
        Assert.Equal(51003, denied.Number);
        var json = await FinancialEvidenceRecoveryTests.InventoryAsync(bills);
        var inventory = JsonSerializer.Deserialize<RecoveryInventory>(json)!;
        Assert.True(Assert.Single(inventory.Rows).FinanciallyProtected);
        var report = await FileRecovery.InspectAsync(inventory, json, recovered.Store, Guid.NewGuid(), default);
        Assert.Empty(report.Orphans);
        if (content == "available") Assert.Empty(report.Missing);
        else Assert.Equal(content == "missing" ? "Missing" : "Corrupt", Assert.Single(report.Missing).Reason);
        await FinancialEvidenceRecoveryTests.AcceptAsync(bills, json, recovered.Store.Alias, JsonSerializer.Serialize(report.Missing));

        // THEN SQL and authorized readback preserve the owner graph and report the actual content state.
        Assert.Equal(before, await RetainedSources(bills));
        var reconcile = await SupplierReconciliationTests.Read(context.Payments);
        Assert.True(reconcile.IsComplete);
        Assert.Equal("4.00", reconcile.Controls.WholeFilterTotals.RefundClearing);
        Assert.Equal(14m, await context.BankBalanceAsync());
        Assert.True(await bills.ScalarAsync<bool>("SELECT Held FROM Storage.Attachments"));
        using var client = bills.Journal.Application.CreateClient();
        await AcquisitionEndpointTests.LoginAsync(client, AuthTestApplication.AdminEmail);
        using var response = await client.GetAsync($"/api/beta/accounting/supplier-open-items/{restored.History.OriginalCredit}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var link = Assert.Single(body.RootElement.GetProperty("financialEvidence").GetProperty("links").EnumerateArray());
        Assert.Equal(id, link.GetProperty("revisionId").GetGuid());
        Assert.Equal(availability, link.GetProperty("availability").GetString());
    }

    private static async Task<(string Journals, string Sources)> RetainedSources(SupplierBillTestContext bills) =>
        (await FinancialEvidencePostingTests.FinancialHashesAsync(bills), await bills.ScalarAsync<string>("""
            SELECT (SELECT * FROM Purchasing.SupplierCredits ORDER BY Id FOR JSON PATH) credits,
              (SELECT * FROM Purchasing.SupplierCashSources ORDER BY Id FOR JSON PATH) cash,
              (SELECT * FROM Purchasing.SupplierCashSettlements ORDER BY Id FOR JSON PATH) settlements,
              (SELECT * FROM Purchasing.SupplierCashSettlementReleases ORDER BY SettlementId FOR JSON PATH) releases,
              (SELECT * FROM Purchasing.SupplierSourceCorrections ORDER BY Id FOR JSON PATH) corrections,
              (SELECT * FROM Purchasing.SupplierSourceCorrectionMembers ORDER BY Id FOR JSON PATH) members,
              (SELECT * FROM Accounting.FinancialEvidenceSets ORDER BY Id FOR JSON PATH) evidence,
              (SELECT * FROM Accounting.FinancialEvidenceLinks ORDER BY Id FOR JSON PATH) links
            FOR JSON PATH,WITHOUT_ARRAY_WRAPPER
            """));
}

public sealed class SupplierCreditRecoveryFixture(SqlServerFixture server) : SupplierCashClearingFixture(server)
{
    internal PurchaseOrderDocumentEndpointTests.TestStorage Source { get; } = new();
    internal (Guid Document, Guid Revision) Document { get; private set; }
    internal Guid CorrectionRequest { get; private set; }
    internal string CorrectionInput { get; private set; } = "";

    internal override async Task<SupplierCashClearingHistory> PrepareAsync(SupplierCreditTestContext context)
    {
        Document = await FinancialEvidencePostingTests.UploadAsync(context.Bills, Source);
        var history = await context.PrepareRefundClearingAsync(Document);
        var repayment = await context.RepaymentCommandAsync([(history.Clearing, "3")]);
        SupplierCreditTestContext.AddRecoveryEvidence(repayment, Document);
        var posted = await context.Bills.ExecuteAsync("RecordSupplierRefundRepayment", Guid.NewGuid(), repayment);
        var source = Guid.Parse(posted["sourceId"]!.ToString());
        var replacement = await context.RepaymentCommandAsync([(history.Clearing, "4")], "2026-09-22");
        var correction = await context.CorrectionCommandAsync("RefundRepayment", source, replacement, "2026-09-22");
        await context.CaptureCashCorrectionPlanAsync("RefundRepayment", correction);
        CorrectionRequest = Guid.NewGuid();
        CorrectionInput = correction.ToJsonString();
        await context.CorrectAsync("RefundRepayment", correction, CorrectionRequest);
        return new(history.Bill, history.OriginalCredit, history.SuccessorCredit, history.Refund, history.Clearing);
    }

    public override Task DisposeAsync()
    {
        Source.Dispose();
        return base.DisposeAsync();
    }
}
