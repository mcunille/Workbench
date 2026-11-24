// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierBillCompleteRecognitionCorrectionTests(SqlServerFixture sqlServer, SupplierPaidMatchedBillFixture fixture)
    : IClassFixture<SupplierPaidMatchedBillFixture>
{
    [Fact]
    public async Task WithdrawnInvoiceCanExplicitlyRetainItsGenuineMatchedReceipt()
    {
        // GIVEN actual goods/service recognition and a paid invoice, with a genuinely reissued receipt revision.
        var (prepared, bill, payment) = await fixture.OpenAsync();
        await using var context = prepared;
        var cash = await context.PaymentCashEvidenceAsync(); var source = await context.BillSourceEvidenceAsync(bill);
        var command = await context.CorrectionCommandAsync("Bill", bill, null);
        var decision = await context.MatchedReceiptDecisionAsync(bill, null, retainWithoutBill: true);
        command["sourceDecisions"]!.AsArray().Add(decision);
        var successorUnit = Guid.Parse(decision["replacement"]!["successorUnitId"]!.ToString());
        await context.Bills.AdminAsync("DELETE FROM [Identity].RoleClaims WHERE ClaimType='workbench/permission' AND ClaimValue IN('SupplierBillsManage','SupplierBillsPost')");
        // WHEN only the invoice is withdrawn and the current producer explicitly authorizes retaining the received service.
        var preview = await context.PreviewCorrectionAsync("Bill", command);
        Assert.Contains(Assert.IsType<JsonArray>(preview["proposedJournals"]), journal =>
            journal!["memberKind"]!.ToString() == "Replacement" && journal["sourceKind"]!.ToString() == "PurchaseRecognition");
        command["expectedPlanFingerprint"] = preview["planFingerprint"]!.DeepClone();
        var correction = await context.CorrectAsync("Bill", command);
        await SupplierCorrectionPreviewContractTests.AssertJournalLinesAsync(context, preview, correction);
        // THEN receipt-only GRNI remains, actual payment becomes an advance and no successor invoice is invented.
        Assert.Equal(0m, await context.BalanceAsync(bill));
        Assert.Equal(100m, await context.BalanceAsync(payment));
        Assert.Equal(-100m, await context.BankBalanceAsync()); Assert.Equal(100m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(-100m, await context.Bills.Recognition.BalanceAsync("GoodsReceivedNotInvoiced"));
        Assert.Equal(0m, await context.Bills.Recognition.BalanceAsync("Prepayment"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierBills"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.ActiveRecognitionSideEvents WHERE UnitId='{successorUnit}' AND Side='Recognition'"));
        Assert.Equal(cash, await context.PaymentCashEvidenceAsync()); Assert.Equal(source, await context.BillSourceEvidenceAsync(bill));
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
    }

    [Fact]
    public async Task ReviewedSuccessorMayUseItsActualNewDocumentDateWithTheOriginalEffectiveDate()
    {
        // GIVEN a real posted invoice and a fresh reviewed correction document issued later.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer); await context.EnableBillCorrectionsAsync();
        var bill = await context.InvoiceOnlyBillAsync("100");
        var replacement = await context.BillReplacementAsync(bill, "90", documentDate: "2026-09-20");
        var command = await context.CorrectionCommandAsync("Bill", bill, replacement);
        // WHEN the normal source owner posts that genuine reviewed successor.
        await context.CaptureBillCorrectionPlanAsync(command); await context.CorrectAsync("Bill", command);
        var successor = Guid.Parse(replacement["post"]!["billId"]!.ToString());
        // THEN the new document date is accurate while the original economic effective date is preserved.
        Assert.Equal("2026-09-20|2026-02-01", await context.Bills.ScalarAsync<string>($"SELECT CONCAT(CONVERT(varchar(10),actual.DocumentDate,23),'|',CONVERT(varchar(10),actual.EffectiveDate,23)) FROM Purchasing.SupplierBillPostingEvents posting JOIN Purchasing.RecognitionSideEvents actual ON actual.TenantId=posting.TenantId AND actual.Id=posting.EventId WHERE posting.BillId='{successor}'"));
        Assert.Equal(90m, await context.BalanceAsync(successor)); Assert.Equal(0m, await context.BalanceAsync(bill));
    }

    [Fact]
    public async Task CompleteBillCorrectionRetainsZeroOnlyInvoiceUnitsWithoutInventingTheirJournals()
    {
        // GIVEN an authentic two-line invoice containing an explicitly reviewed zero-price whole unit.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer); await context.EnableBillCorrectionsAsync();
        var draft = context.Bills.CompleteDraft(Guid.NewGuid().ToString()); var revision = draft["revision"]!;
        revision["total"] = "100"; revision["postingDate"] = "2026-09-15";
        var first = revision["units"]![0]!; first["classification"] = "Expense"; first["components"]![1]!["amount"] = "0";
        var zero = first.DeepClone(); zero["componentKey"] = "line-2"; zero["goodsReference"] = "Goods-2";
        zero["unitId"] = Guid.NewGuid().ToString(); zero["components"]![0]!["amount"] = "0";
        revision["units"]!.AsArray().Add(zero);
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context.Bills, draft);
        await context.Bills.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.Bills.PostCommand(reviewed));
        var bill = Guid.Parse(reviewed["billId"]!.ToString());
        var replacement = await context.BillReplacementAsync(bill, "90", unitCosts: new Dictionary<string, string> { ["line-1"] = "90", ["line-2"] = "0" });
        var command = await context.CorrectionCommandAsync("Bill", bill, replacement);
        // WHEN all real whole units are corrected together, including the zero-only side.
        var preview = await context.PreviewCorrectionAsync("Bill", command);
        Assert.Equal(2, Assert.IsType<JsonArray>(preview["proposedJournals"]).Count);
        command["expectedPlanFingerprint"] = preview["planFingerprint"]!.DeepClone();
        var correction = await context.CorrectAsync("Bill", command);
        await SupplierCorrectionPreviewContractTests.AssertJournalLinesAsync(context, preview, correction);
        var successor = Guid.Parse(replacement["post"]!["billId"]!.ToString());
        // THEN both successor identities/links survive while only the positive obligation creates a journal.
        Assert.Equal(2, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.SupplierBillPostingEvents WHERE BillId='{successor}'"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.SupplierBillPostingEvents posting JOIN Purchasing.RecognitionSideEvents actual ON actual.TenantId=posting.TenantId AND actual.Id=posting.EventId WHERE posting.BillId='{successor}' AND actual.JournalId IS NULL AND actual.SourceAmount=0"));
        Assert.Equal(2, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.SupplierBillPostingEvents posting JOIN Purchasing.RecognitionEventCorrections link ON link.TenantId=posting.TenantId AND link.OriginalEventId=posting.EventId WHERE posting.BillId='{bill}' AND link.ReplacementEventId IS NOT NULL"));
        Assert.Equal(0m, await context.BalanceAsync(bill)); Assert.Equal(90m, await context.BalanceAsync(successor));
        Assert.Equal(90m, await context.Bills.Recognition.BalanceAsync("Prepayment"));
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
    }

    [Fact]
    public async Task CurrentProducerCannotChangeAnOriginalMatchedEffectiveDate()
    {
        // GIVEN actual source records and a current registered producer revision with an incorrect effective-date output.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer); await context.EnableBillCorrectionsAsync();
        var bill = await context.ExpenseBillAsync("100"); var replacement = await context.BillReplacementAsync(bill, "90");
        var command = await context.CorrectionCommandAsync("Bill", bill, replacement);
        command["sourceDecisions"]!.AsArray().Add(await context.MatchedReceiptDecisionAsync(bill, replacement));
        await context.Bills.AdminAsync("""
            DECLARE @definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.ValidateFixtureRecognitionOwner'));
            IF CHARINDEX(N'actual.EffectiveDate effectiveDate',@definition)=0 THROW 51000,'Current producer date anchor unavailable.',1;
            SET @definition=REPLACE(@definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @definition=REPLACE(@definition,N'actual.EffectiveDate effectiveDate',N'DATEADD(day,1,actual.EffectiveDate) effectiveDate');
            EXEC sys.sp_executesql @definition;
            UPDATE Purchasing.RecognitionSourceOwners SET DefinitionSha256=HASHBYTES('SHA2_256',CONVERT(varbinary(max),OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.ValidateFixtureRecognitionOwner'))))
              WHERE PostingPermission=N'PurchaseRecognitionFixturePost';
            """);
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);
        // WHEN bill composition validates the complete current source result THEN BK-04 original effective dates remain mandatory.
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Bill", command))).Number);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
    }
}
