// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierBillCreditDependencyTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task ReviewedBillAndCreditSuccessorsReapplyOnlyTheChosenRefundDebt()
    {
        // GIVEN a real refunded credit and complete reviewed successor bill intent.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer); await context.EnableBillCorrectionsAsync();
        var bill = await context.ExpenseBillAsync("100"); var original = await context.CreditAsync(bill, "18");
        var creditId = Guid.Parse(original["sourceId"]!.ToString());
        var clearing = Guid.Parse((await context.RefundAsync([(creditId, "18")]))["settlementIds"]![0]!.ToString());
        var cash = await context.CashEvidenceAsync(); var replacement = await context.BillReplacementAsync(bill, "90");
        var successor = Guid.Parse(replacement["post"]!["billId"]!.ToString());
        var newCredit = await context.CreditCommandAsync(bill, "10", "2026-09-20");
        var newCreditId = Guid.Parse(newCredit["creditId"]!.ToString()); var claim = newCredit["claims"]![0]!;
        claim["billId"] = successor.ToString(); claim["billRevisionId"] = replacement["post"]!["revisionId"]!.DeepClone();
        claim["recognitionEventId"] = replacement["unitBindings"]![0]!["invoiceEventId"]!.DeepClone();
        var command = await context.CorrectionCommandAsync("Bill", bill, replacement);
        command["sourceDecisions"]!.AsArray().Add(await context.MatchedReceiptDecisionAsync(bill, replacement));
        command["sourceDecisions"]!.AsArray().Add(new JsonObject
        {
            ["sourceKind"] = "SupplierCredit",
            ["sourceId"] = creditId.ToString(),
            ["expectedSourceVersion"] = (await context.CorrectionCommandAsync("Credit", creditId, null))["expectedSourceVersion"]!.DeepClone(),
            ["replacement"] = newCredit
        });
        command["reapplications"]!.AsArray().Add(new JsonObject
        {
            ["fundingItemId"] = newCreditId.ToString(),
            ["debtItemId"] = clearing.ToString(),
            ["amount"] = "10"
        });
        // WHEN the preview derives future credit capacity from the real reviewed bill and exact fresh event bindings.
        await context.CaptureBillCorrectionPlanAsync(command); await context.CorrectAsync("Bill", command);
        // THEN authentic successors replace old sources, while the unchanged eighteen-dollar receipt leaves debt eight.
        Assert.Equal(0m, await context.BalanceAsync(bill)); Assert.Equal(0m, await context.BalanceAsync(creditId));
        Assert.Equal(90m, await context.BalanceAsync(successor)); Assert.Equal(0m, await context.BalanceAsync(newCreditId));
        Assert.Equal(8m, await context.BalanceAsync(clearing)); Assert.Equal(18m, await context.BankBalanceAsync());
        Assert.Equal(80m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(cash, await context.CashEvidenceAsync());
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
    }

    [Fact]
    public async Task BillInverseAlsoUnwindsEarlierCreditRecognitionRemeasurement()
    {
        // GIVEN invoice-first recognition and an already corrected credit with a real monetary matching adjustment.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer); await context.EnableBillCorrectionsAsync();
        var (bill, _, unit) = await context.PrepaymentBillAsync("100");
        var first = await context.CreditAsync(bill, "18"); var firstId = Guid.Parse(first["sourceId"]!.ToString());
        await context.RecognizeAsync(unit, "100");
        var surviving = await context.CreditCommandAsync(bill, "10", "2026-09-20"); var survivingId = Guid.Parse(surviving["creditId"]!.ToString());
        var creditCommand = await context.CorrectionCommandAsync("Credit", firstId, surviving);
        await context.CaptureCorrectionPlanAsync(creditCommand); await context.CorrectAsync("Credit", creditCommand);
        Assert.Equal(90m, await context.Bills.Recognition.BalanceAsync("Expense"));
        var replacement = await context.BillReplacementAsync(bill, "90", "2026-09-22");
        var command = await context.CorrectionCommandAsync("Bill", bill, replacement, "2026-09-22");
        command["sourceDecisions"]!.AsArray().Add(await context.MatchedReceiptDecisionAsync(bill, replacement));
        command["sourceDecisions"]!.AsArray().Add(new JsonObject
        {
            ["sourceKind"] = "SupplierCredit",
            ["sourceId"] = survivingId.ToString(),
            ["expectedSourceVersion"] = (await context.CorrectionCommandAsync("Credit", survivingId, null))["expectedSourceVersion"]!.DeepClone(),
            ["replacement"] = null
        });
        // WHEN the entire bill group is replaced THEN its original monetary adjustments cannot strand prepayment.
        await context.CaptureBillCorrectionPlanAsync(command); await context.CorrectAsync("Bill", command);
        Assert.Equal(90m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(0m, await context.Bills.Recognition.BalanceAsync("Prepayment"));
        Assert.Equal(0m, await context.BalanceAsync(firstId)); Assert.Equal(0m, await context.BalanceAsync(survivingId));
        Assert.Equal(90m, await context.BalanceAsync(Guid.Parse(replacement["post"]!["billId"]!.ToString())));
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BillCorrectionRequiresEveryDependentCreditAndPreservesActualRefund(bool refunded)
    {
        // GIVEN a real matched bill with an authentic credit, optionally consumed by an actual refund.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        await context.EnableBillCorrectionsAsync();
        var bill = await context.ExpenseBillAsync("100"); var credit = await context.CreditAsync(bill, "18");
        var creditId = Guid.Parse(credit["sourceId"]!.ToString()); Guid? clearing = null;
        if (refunded) clearing = Guid.Parse((await context.RefundAsync([(creditId, "18")]))["settlementIds"]![0]!.ToString());
        var cash = await context.CashEvidenceAsync(); var source = await context.CreditEvidenceAsync(creditId);
        var replacement = await context.BillReplacementAsync(bill, "90"); var successor = Guid.Parse(replacement["post"]!["billId"]!.ToString());
        var command = await context.CorrectionCommandAsync("Bill", bill, replacement);
        command["sourceDecisions"]!.AsArray().Add(await context.MatchedReceiptDecisionAsync(bill, replacement));
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);
        // WHEN any active dependent credit is omitted THEN preview fails without a partial correction.
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Bill", command))).Number);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
        var creditDecision = await context.CorrectionCommandAsync("Credit", creditId, null);
        command["sourceDecisions"]!.AsArray().Add(new JsonObject
        {
            ["sourceKind"] = "SupplierCredit",
            ["sourceId"] = creditId.ToString(),
            ["expectedSourceVersion"] = creditDecision["expectedSourceVersion"]!.DeepClone(),
            ["replacement"] = null
        });
        // WHEN the complete proposal explicitly reverses that credit alongside both bill recognition sides.
        await context.CaptureBillCorrectionPlanAsync(command); var result = await context.CorrectAsync("Bill", command);
        var group = Guid.Parse(result["groupId"]!.ToString());
        // THEN all old sources close while actual refunded cash becomes an explicit clearing debt.
        Assert.Equal(0m, await context.BalanceAsync(bill)); Assert.Equal(0m, await context.BalanceAsync(creditId));
        Assert.Equal(90m, await context.BalanceAsync(successor)); Assert.Equal(90m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(0m, await context.Bills.Recognition.BalanceAsync("Prepayment"));
        Assert.Equal(refunded ? 18m : 0m, await context.BankBalanceAsync());
        Assert.Equal(cash, await context.CashEvidenceAsync()); Assert.Equal(source, await context.CreditEvidenceAsync(creditId));
        if (clearing.HasValue) Assert.Equal(18m, await context.BalanceAsync(clearing.Value));
        Assert.Equal(2, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.SupplierSourceCorrections WHERE GroupId='{group}'"));
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
    }
}
