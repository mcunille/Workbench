// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierOpenItemSourceTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task DerivationRequiresStoredBillRequestOwnershipAndIsIdempotent()
    {
        // GIVEN two genuinely posted bills with different recognition receipts.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        var first = await SupplierBillPostingTests.ReviewedAsync(context);
        await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(first));
        var second = await SupplierBillPostingTests.ReviewedAsync(context, context.CompleteDraft("INV-2"));
        await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(second));
        var request = await context.ScalarAsync<Guid>($"SELECT RecognitionRequestId FROM Purchasing.SupplierBillPostings WHERE BillId='{first["billId"]}'");
        // WHEN rederived with its stored owner THEN the same items remain; a different caller-selected bill is rejected.
        await context.AdminAsync(DeriveSql(request, first["billId"]!.GetValue<string>()));
        var error = await Assert.ThrowsAsync<SqlException>(() => context.AdminAsync(DeriveSql(request, second["billId"]!.GetValue<string>())));
        Assert.Equal(51004, error.Number);
        Assert.Equal(2, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierOpenItems"));
        Assert.Equal(220m, await context.ScalarAsync<decimal>("SELECT SUM(Amount) FROM Purchasing.SupplierItemMovements"));
    }

    private static string DeriveSql(Guid request, string billId) => $"""
        BEGIN TRY
          BEGIN TRAN;
          DECLARE @Tenant uniqueidentifier='{JournalTestContext.TenantId}',@Resource nvarchar(255);
          SET @Resource=N'Accounting:'+CONVERT(nvarchar(36),@Tenant);
          EXEC sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction';
          EXEC Purchasing.DeriveRecognitionOpenItems '{JournalTestContext.TenantId}','{request}','{billId}';
          COMMIT;
        END TRY BEGIN CATCH IF @@TRANCOUNT>0 ROLLBACK; THROW; END CATCH;
        """;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PostedBillOwnsExactlyOnePayable(bool receiptFirst, bool multipleUnits)
    {
        // GIVEN a reviewed bill for 306.60, optionally spanning units or matching a receipt.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        var draft = context.CompleteDraft();
        var units = draft["revision"]!["units"]!.AsArray();
        units[0]!["components"]![0]!["amount"] = multipleUnits ? "139.30" : "278.60";
        units[0]!["components"]![1]!["amount"] = multipleUnits ? "14.00" : "28.00";
        draft["revision"]!["total"] = "306.60";
        if (multipleUnits)
        {
            var second = units[0]!.DeepClone(); second["unitId"] = Guid.NewGuid().ToString();
            second["componentKey"] = "second"; units.Add(second);
        }
        if (receiptFirst)
        {
            var receipt = await context.Recognition.CommandAsync(cost: "278.60");
            await context.Recognition.PostAsync(receipt.ToJsonString());
            units[0]!["unitId"] = receipt["units"]![0]!["unitId"]!.DeepClone();
            units[0]!["expectedPriorEventRevision"] = 1;
        }
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context, draft);
        var command = context.PostCommand(reviewed); var request = Guid.NewGuid();
        // WHEN the source posts and retries THEN one bill owns all payable control evidence exactly once.
        var posted = await context.ExecuteAsync("PostSupplierBill", request, command);
        Assert.Equal(306.60m, await context.ScalarAsync<decimal>("SELECT COALESCE(SUM(Amount),0) FROM Purchasing.SupplierItemMovements"));
        Assert.Equal(1, await context.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.SupplierOpenItems WHERE BillId='{reviewed["billId"]}'"));
        Assert.Equal(0m, await context.ScalarAsync<decimal>("SELECT COALESCE(SUM(a.Amount),0) FROM Purchasing.SupplierControlAttributions a JOIN Purchasing.SupplierItemMovements m ON m.Id=a.MovementId JOIN Purchasing.SupplierOpenItems i ON i.Id=m.ItemId WHERE i.BillId IS NULL"));
        Assert.Equal(posted.ToJsonString(), (await context.ExecuteAsync("PostSupplierBill", request, command)).ToJsonString());
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierControlAttributions WHERE AccountPurpose<>'SupplierPayable'"));
    }

    [Fact]
    public async Task ZeroBillRetainsSourceWithoutMonetaryItem()
    {
        // GIVEN a reviewed zero-value invoice.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        var draft = context.CompleteDraft(); draft["revision"]!["total"] = "0";
        foreach (var component in draft["revision"]!["units"]![0]!["components"]!.AsArray()) component!["amount"] = "0";
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context, draft);
        // WHEN posted THEN source evidence survives without fabricated monetary capacity.
        await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
        Assert.Equal(1, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierBillPostings"));
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierOpenItems"));
    }

    [Fact]
    public async Task UnbilledRecognitionRemainsUnallocatable()
    {
        // GIVEN a receipt accrual and an unrelated invoice without a structured bill.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var receipt = await context.CommandAsync(); await context.PostAsync(receipt.ToJsonString());
        var invoice = await context.CommandAsync("Invoice", cost: "306.60");
        // WHEN the invoice posts THEN only its AP is indexed, with absent bill identity.
        await context.PostAsync(invoice.ToJsonString());
        Assert.Equal(306.60m, await PurchaseRecognitionCorrectionTests.ScalarAsync<decimal>(context, "SELECT COALESCE(SUM(Amount),0) FROM Purchasing.SupplierItemMovements"));
        Assert.Equal(1, await PurchaseRecognitionCorrectionTests.ScalarAsync<int>(context, "SELECT COUNT(*) FROM Purchasing.SupplierOpenItems WHERE BillId IS NULL AND Kind='Payable'"));
        Assert.Equal(1, await PurchaseRecognitionCorrectionTests.ScalarAsync<int>(context, "SELECT COUNT(*) FROM Purchasing.SupplierOpenItems"));
    }

    [Theory]
    [InlineData("306.60", 306.60)]
    [InlineData("0", 0)]
    public async Task RecognitionCorrectionPreservesHistory(string amount, decimal originalAmount)
    {
        // GIVEN unallocated invoice evidence and its exact original receipt.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var invoice = await context.CommandAsync("Invoice", cost: amount); var request = Guid.NewGuid();
        await context.PostAsync(invoice.ToJsonString(), request);
        var original = await PurchaseRecognitionCorrectionTests.ScalarAsync<string>(context, "SELECT ResultJson FROM Purchasing.RecognitionGroupReceipts");
        var correction = await PurchaseRecognitionCorrectionTests.CorrectionAsync(context, invoice, "280");
        // WHEN replaced in March THEN February remains unchanged and the retained original is exactly inverted.
        await context.CorrectAsync(correction.ToJsonString());
        Assert.Equal(originalAmount, await PurchaseRecognitionCorrectionTests.ScalarAsync<decimal>(context, "SELECT COALESCE(SUM(Amount),0) FROM Purchasing.SupplierItemMovements WHERE PostingDate<'2026-03-01'"));
        Assert.Equal(280m, await PurchaseRecognitionCorrectionTests.ScalarAsync<decimal>(context, "SELECT COALESCE(SUM(Amount),0) FROM Purchasing.SupplierItemMovements"));
        Assert.Equal(-originalAmount, await PurchaseRecognitionCorrectionTests.ScalarAsync<decimal>(context, "SELECT COALESCE(SUM(Amount),0) FROM Purchasing.SupplierItemMovements WHERE EventKind='ReverseSource'"));
        Assert.Equal(1, await PurchaseRecognitionCorrectionTests.ScalarAsync<int>(context, "SELECT COUNT(*) FROM Purchasing.SupplierFinancialGroups WHERE Operation='CorrectSource'"));
        Assert.Equal(original, await PurchaseRecognitionCorrectionTests.ScalarAsync<string>(context, $"SELECT ResultJson FROM Purchasing.RecognitionGroupReceipts WHERE RequestId='{request}'"));
        Assert.Equal(0, await PurchaseRecognitionCorrectionTests.ScalarAsync<int>(context, "SELECT COUNT(*) FROM Purchasing.SupplierItemMovements m JOIN Accounting.SourceEvents s ON s.Id=m.SourceEventId WHERE m.RecordedAtUtc<>s.RecordedAtUtc"));
    }

    [Fact]
    public async Task ItemFailureRollsBackPostedBillAndReceipt()
    {
        // GIVEN a reviewed bill and a disposable interruption of the new source integration boundary.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context);
        await context.AdminAsync("CREATE TRIGGER Purchasing.InterruptItem ON Purchasing.SupplierOpenItems AFTER INSERT AS THROW 51999,'Disposable interruption',1;");
        // WHEN item persistence fails THEN journal, bill posting and success receipt all roll back.
        Assert.Equal(51999, (await Assert.ThrowsAsync<SqlException>(() => context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed)))).Number);
        Assert.Equal(0, await context.Journal.CountAsync("JournalEntries"));
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierBillPostings"));
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierBillReceipts WHERE Operation='Post'"));
    }
}
