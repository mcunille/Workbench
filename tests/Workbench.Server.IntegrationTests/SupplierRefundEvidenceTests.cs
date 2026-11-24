// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierRefundEvidenceTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptEvidenceBindsExactRevisionOrRejectsAtomically(bool removed)
    {
        // GIVEN real refundable funding and a private document revision on its purchase.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var deposit = await context.Payments.CommandAsync("100"); await context.Payments.RecordAsync(deposit);
        var advance = Guid.Parse(deposit["paymentId"]!.GetValue<string>());
        var document = await context.Bills.SeedDocumentAsync();
        var command = await context.RefundCommandAsync([(advance, "40")]);
        command["evidence"] = new JsonObject
        {
            ["documents"] = new JsonArray(new JsonObject { ["documentId"] = document.DocumentId.ToString(), ["revisionId"] = document.RevisionId.ToString() }),
            ["missingEvidenceReason"] = null
        };
        if (removed) await context.Bills.AdminAsync($"UPDATE Purchasing.PurchaseOrderDocuments SET RemovedAtUtc=SYSUTCDATETIME() WHERE Id='{document.DocumentId}'");
        // WHEN the actual receipt is recorded THEN only available immutable evidence can acquire a hold.
        if (removed)
        {
            var error = await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync("RecordSupplierRefund", Guid.NewGuid(), command));
            Assert.Equal(51004, error.Number);
            Assert.Equal(100m, await context.BalanceAsync(advance));
            Assert.Equal(-100m, await context.BankBalanceAsync());
            Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCashSources"));
            Assert.False(await context.Bills.ScalarAsync<bool>($"SELECT Held FROM Storage.Attachments WHERE Id=(SELECT AttachmentId FROM Purchasing.PurchaseOrderDocuments WHERE Id='{document.DocumentId}')"));
            return;
        }
        var result = await context.Bills.ExecuteAsync("RecordSupplierRefund", Guid.NewGuid(), command);
        var id = Guid.Parse(result["sourceId"]!.GetValue<string>());
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Accounting.FinancialEvidenceSets evidence JOIN Accounting.FinancialEvidenceLinks link ON link.TenantId=evidence.TenantId AND link.EvidenceSetId=evidence.Id WHERE evidence.OwnerKind='SupplierRefund' AND evidence.OwnerId='{id}' AND link.DocumentId='{document.DocumentId}' AND link.RevisionId='{document.RevisionId}' AND link.Sha256=REPLICATE('A',64) AND link.Length=4"));
        Assert.True(await context.Bills.ScalarAsync<bool>($"SELECT Held FROM Storage.Attachments WHERE Id=(SELECT AttachmentId FROM Purchasing.PurchaseOrderDocuments WHERE Id='{document.DocumentId}')"));
    }
}
