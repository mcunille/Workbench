// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierBillQueryTests(SqlServerFixture sqlServer)
{
    private static async Task<SupplierBillTestContext> OpenAsync(SqlServerFixture fixture)
    {
        return await SupplierBillPostingTests.OpenAsync(fixture);
    }
    internal static async Task<JsonObject> ReadAsync(SupplierBillTestContext context, string procedure, params (string Name, object Value)[] parameters)
    {
        await using var command = new SqlCommand($"Purchasing.{procedure}", context.Journal.Connection) { CommandType = System.Data.CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@ActorId", JournalTestContext.ActorId); command.Parameters.AddWithValue("@SessionId", context.Journal.SessionId);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return JsonNode.Parse((string)(await command.ExecuteScalarAsync())!)!.AsObject();
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
        var page = await ReadAsync(context, "ReadSupplierBills", ("@PurchaseOrderId", context.Recognition.PurchaseOrderId), ("@Take", 2));
        Assert.Equal(2, page["items"]?.AsArray().Count);
        var next = await ReadAsync(context, "ReadSupplierBills", ("@PurchaseOrderId", context.Recognition.PurchaseOrderId), ("@Take", 2), ("@AfterId", Guid.Parse(page["nextId"]!.GetValue<string>())));
        Assert.Single(next["items"]!.AsArray());
        Assert.Equal(3, page["items"]!.AsArray().Concat(next["items"]!.AsArray()).Select(x => x!["billId"]!.ToString()).Distinct().Count());
        var id = Guid.Parse(posted["billId"]!.GetValue<string>());
        var detail = await ReadAsync(context, "ReadSupplierBill", ("@BillId", id));
        // THEN evidence and result still identify the immutable financial source, with no paid/outstanding invention.
        Assert.Equal("Posted", detail["state"]!.GetValue<string>());
        Assert.Equal(posted["journalIds"]!.ToJsonString(), detail["posting"]!["journalIds"]!.ToJsonString());
        Assert.Equal("110.00", detail["revision"]!["total"]!.GetValue<string>());
        var history = await ReadAsync(context, "ReadSupplierBillHistory", ("@BillId", id), ("@Take", 2));
        Assert.Equal(2, history["items"]!.AsArray().Count);
        var remaining = await ReadAsync(context, "ReadSupplierBillHistory", ("@BillId", id), ("@Take", 2), ("@AfterSequence", history["nextSequence"]!.GetValue<long>()));
        Assert.Equal(2, remaining["items"]!.AsArray().Count);
    }
    [Fact]
    public async Task ReadPermissionAndPageBoundsAreEnforcedInSql()
    {
        // GIVEN a reader with management/report permissions in the same tenant.
        await using var context = await OpenAsync(sqlServer);
        // WHEN an unbounded page is requested THEN SQL rejects it.
        Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => ReadAsync(context, "ReadSupplierBills", ("@PurchaseOrderId", context.Recognition.PurchaseOrderId), ("@Take", 101)))).Number);
        // AND loss of both business/report authority prevents even empty list readback.
        await context.AdminAsync("DELETE [Identity].RoleClaims WHERE ClaimValue IN(N'SupplierBillsManage',N'AccountingReportsRead')");
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => ReadAsync(context, "ReadSupplierBills", ("@PurchaseOrderId", context.Recognition.PurchaseOrderId)))).Number);
    }

    [Fact]
    public async Task RemovedEvidenceChangesAvailabilityWithoutRewritingPostedSnapshot()
    {
        // GIVEN immutable private document metadata linked to a reviewed supplier invoice.
        await using var context = await OpenAsync(sqlServer);
        var documentId = Guid.NewGuid(); var revisionId = Guid.NewGuid(); var attachmentId = Guid.NewGuid();
        await context.AdminAsync($"""
            INSERT Storage.Attachments(Id,TenantId,CreatedAtUtc) VALUES('{attachmentId}','{JournalTestContext.TenantId}',SYSUTCDATETIME());
            INSERT Storage.Revisions(Id,TenantId,AttachmentId,OperationId,ActorUserId,ProviderAlias,Source,MediaType,Length,Sha256,State,CreatedAtUtc)
              VALUES('{revisionId}','{JournalTestContext.TenantId}','{attachmentId}',NEWID(),'{JournalTestContext.ActorId}','local','UserUpload','application/pdf',4,REPLICATE('A',64),1,SYSUTCDATETIME());
            UPDATE Storage.Attachments SET CurrentRevisionId='{revisionId}' WHERE Id='{attachmentId}';
            INSERT Purchasing.PurchaseOrderDocuments(Id,TenantId,OrderId,AttachmentId,RevisionId,Label,MediaType,Extension,Length,Sha256,CreatedAtUtc)
              VALUES('{documentId}','{JournalTestContext.TenantId}','{context.Recognition.PurchaseOrderId}','{attachmentId}','{revisionId}','Invoice','application/pdf','.pdf',4,REPLICATE('A',64),SYSUTCDATETIME());
            """);
        var draft = context.CompleteDraft();
        draft["revision"]!["documents"] = new JsonArray(new JsonObject { ["documentId"] = documentId.ToString(), ["revisionId"] = revisionId.ToString() });
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context, draft);
        var posted = await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
        var id = Guid.Parse(posted["billId"]!.GetValue<string>());
        var before = await ReadAsync(context, "ReadSupplierBill", ("@BillId", id));
        Assert.True(before["evidence"]?[0]?["available"]?.GetValue<bool>());
        // WHEN the document metadata records subsequent ordinary removal (BK-07 holds are not delivered).
        await context.AdminAsync($"UPDATE Purchasing.PurchaseOrderDocuments SET RemovedAtUtc=SYSUTCDATETIME() WHERE Id='{documentId}'");
        var after = await ReadAsync(context, "ReadSupplierBill", ("@BillId", id));
        // THEN live availability changes while immutable financial and document evidence survives.
        Assert.False(after["evidence"]![0]!["available"]!.GetValue<bool>());
        Assert.Equal(new string('A', 64), after["evidence"]![0]!["digest"]!.GetValue<string>());
        Assert.Equal(before["posting"]!.ToJsonString(), after["posting"]!.ToJsonString());
    }
}
