// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierBillQueryTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData("Revise")]
    [InlineData("Abandon")]
    public async Task HistoricalReviewRetainsSnapshotsAndCurrentAvailability(string operation)
    {
        // GIVEN an immutable reviewed document whose current review pointer will subsequently be cleared.
        await using var context = await OpenAsync(sqlServer);
        var (document, revision) = await context.SeedDocumentAsync();
        var draft = context.CompleteDraft();
        draft["revision"]!["documents"] = new JsonArray(new JsonObject { ["documentId"] = document.ToString(), ["revisionId"] = revision.ToString() });
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context, draft);
        var id = Guid.Parse(reviewed["billId"]!.ToString());
        await context.SaveAsync(Guid.NewGuid(), context.Change(reviewed, operation, operation == "Revise" ? context.CompleteDraft()["revision"]!.AsObject() : null));
        var before = await context.ReadAsync("ReadSupplierBillHistory", ("@BillId", id), ("@Take", 100));
        var previous = before["items"]!.AsArray().Single(x => x!["operation"]!.ToString() == "Review")!;
        // WHEN the linked file is later removed THEN historical readback retains its reviewed identity and reports the loss.
        Assert.Equal(new string('A', 64), previous["review"]?["evidence"]?[0]?["digest"]?.GetValue<string>());
        Assert.True(previous["review"]!["evidence"]![0]!["available"]!.GetValue<bool>());
        await context.AdminAsync($"UPDATE Purchasing.PurchaseOrderDocuments SET RemovedAtUtc=SYSUTCDATETIME(),Label='Later label' WHERE Id='{document}'");
        var after = await context.ReadAsync("ReadSupplierBillHistory", ("@BillId", id), ("@Take", 100));
        var history = after["items"]!.AsArray().Single(x => x!["operation"]!.ToString() == "Review")!;
        Assert.Equal("Invoice", history["review"]!["evidence"]![0]!["label"]!.GetValue<string>());
        Assert.False(history["review"]!["evidence"]![0]!["available"]!.GetValue<bool>());
        Assert.Equal(context.Recognition.PurchaseOrderVersion, history["review"]!["purchaseOrderVersion"]!.GetValue<string>());
        Assert.Equal("Recognition supplier", history["revision"]!["supplierName"]!.GetValue<string>());
        Assert.Equal(1, history["revision"]!["purchaseOrderRevision"]!.GetValue<int>());
    }

    private static async Task<SupplierBillTestContext> OpenAsync(SqlServerFixture fixture)
    {
        return await SupplierBillPostingTests.OpenAsync(fixture);
    }
    [Fact]
    public async Task BoundedListHistoryAndPostedDetailTraceImmutableSource()
    {
        // GIVEN three bills and one edited then reviewed/posted source.
        await using var context = await OpenAsync(sqlServer);
        var original = await context.SaveAsync(Guid.NewGuid(), context.CompleteDraft());
        var edited = await context.SaveAsync(Guid.NewGuid(), context.Change(original, "Revise", context.CompleteDraft()["revision"]!.AsObject()));
        var reviewed = await context.ReviewAsync(Guid.NewGuid(), context.ReviewCommand(edited));
        var posted = await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
        await context.SaveAsync(Guid.NewGuid(), context.DraftCommand("INV-2"));
        await context.SaveAsync(Guid.NewGuid(), context.DraftCommand("INV-3"));
        // WHEN list/history are read in bounded pages and posted detail is followed.
        var page = await context.ReadAsync("ReadSupplierBills", ("@PurchaseOrderId", context.Recognition.PurchaseOrderId), ("@Take", 2));
        Assert.Equal(2, page["items"]?.AsArray().Count);
        var next = await context.ReadAsync("ReadSupplierBills", ("@PurchaseOrderId", context.Recognition.PurchaseOrderId), ("@Take", 2), ("@AfterId", Guid.Parse(page["nextId"]!.GetValue<string>())));
        Assert.Single(next["items"]!.AsArray());
        Assert.Equal(3, page["items"]!.AsArray().Concat(next["items"]!.AsArray()).Select(x => x!["billId"]!.ToString()).Distinct().Count());
        var id = Guid.Parse(posted["billId"]!.GetValue<string>());
        var detail = await context.ReadDetailAsync(id);
        // THEN evidence and result still identify the immutable financial source, with no paid/outstanding invention.
        Assert.Equal("Posted", detail.State);
        Assert.Equal(posted["journalIds"]!.ToJsonString(), detail.Posting!.Value.GetProperty("journalIds").GetRawText());
        Assert.Equal("110.00", detail.Revision.GetProperty("total").GetString());
        var history = await context.ReadAsync("ReadSupplierBillHistory", ("@BillId", id), ("@Take", 2));
        Assert.Equal(2, history["items"]!.AsArray().Count);
        var remaining = await context.ReadAsync("ReadSupplierBillHistory", ("@BillId", id), ("@Take", 2), ("@AfterSequence", history["nextSequence"]!.GetValue<long>()));
        Assert.Equal(2, remaining["items"]!.AsArray().Count);
    }
    [Fact]
    public async Task ReadPermissionAndPageBoundsAreEnforcedInSql()
    {
        // GIVEN a reader with management/report permissions in the same tenant.
        await using var context = await OpenAsync(sqlServer);
        // WHEN an unbounded page is requested THEN SQL rejects it.
        Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => context.ReadAsync("ReadSupplierBills", ("@PurchaseOrderId", context.Recognition.PurchaseOrderId), ("@Take", 101)))).Number);
        // AND loss of both business/report authority prevents even empty list readback.
        await context.AdminAsync("DELETE [Identity].RoleClaims WHERE ClaimValue IN(N'SupplierBillsManage',N'AccountingReportsRead')");
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => context.ReadAsync("ReadSupplierBills", ("@PurchaseOrderId", context.Recognition.PurchaseOrderId)))).Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableEvidenceDoesNotRewritePostedSnapshot(bool recoveryMissing)
    {
        // GIVEN immutable private document metadata linked to a reviewed supplier invoice.
        await using var context = await OpenAsync(sqlServer);
        var (documentId, revisionId) = await context.SeedDocumentAsync();
        var draft = context.CompleteDraft();
        draft["revision"]!["documents"] = new JsonArray(new JsonObject { ["documentId"] = documentId.ToString(), ["revisionId"] = revisionId.ToString() });
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context, draft);
        var posted = await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
        var id = Guid.Parse(posted["billId"]!.GetValue<string>());
        var before = await context.ReadAsync("ReadSupplierBill", ("@BillId", id));
        Assert.True(before["evidence"]?[0]?["available"]?.GetValue<bool>());
        // WHEN the document metadata records subsequent ordinary removal (BK-07 holds are not delivered).
        await context.AdminAsync(recoveryMissing
            ? $"INSERT Storage.RecoveryFiles(TenantId,RevisionId,ReportId,Generation,Reason,AcceptedAtUtc) VALUES('{JournalTestContext.TenantId}','{revisionId}',NEWID(),1,'Missing',SYSUTCDATETIME())"
            : $"UPDATE Purchasing.PurchaseOrderDocuments SET RemovedAtUtc=SYSUTCDATETIME() WHERE Id='{documentId}'");
        var after = await context.ReadAsync("ReadSupplierBill", ("@BillId", id));
        // THEN live availability changes while immutable financial and document evidence survives.
        Assert.False(after["evidence"]![0]!["available"]!.GetValue<bool>());
        Assert.Equal(new string('A', 64), after["evidence"]![0]!["digest"]!.GetValue<string>());
        Assert.Equal(before["posting"]!.ToJsonString(), after["posting"]!.ToJsonString());
    }
}
