// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed partial class SupplierCreditCorrectionTests(SqlServerFixture sqlServer)
{
    private SqlServerFixture Fixture => sqlServer;
    [Fact]
    public async Task RefundedCredit18CorrectedTo10PreservesCashAndLeavesDebt()
    {
        // GIVEN a paid bill, authentic credit and actual refund of eighteen.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var bill = await context.ExpenseBillAsync("100");
        var payment = await context.Payments.CommandAsync("100", "2026-09-16");
        await context.Payments.AllocateAsync(payment, bill, "100"); await context.Payments.RecordAsync(payment);
        var credit = await context.CreditAsync(bill, "18");
        var original = Guid.Parse(credit["sourceId"]!.GetValue<string>());
        var receipt = await context.RefundAsync([(original, "18")]);
        var settlement = Guid.Parse(receipt["settlementIds"]![0]!.GetValue<string>());
        var cashBefore = await context.CashEvidenceAsync(); var originalBefore = await context.CreditEvidenceAsync(original);
        var replacement = await context.CreditCommandAsync(bill, "10", "2026-09-20");
        var successor = Guid.Parse(replacement["creditId"]!.GetValue<string>());
        var command = await context.CorrectionCommandAsync("Credit", original, replacement);
        command["reapplications"]!.AsArray().Add(new JsonObject
        {
            ["fundingItemId"] = successor.ToString(),
            ["debtItemId"] = settlement.ToString(),
            ["amount"] = "10"
        });
        // WHEN the complete source correction explicitly reapplies ten to the released receipt debt.
        await context.CaptureCorrectionPlanAsync(command);
        var result = await context.CorrectAsync("Credit", command);
        var group = Guid.Parse(result["groupId"]!.GetValue<string>());
        // THEN actual cash and original evidence remain exact while eight is owed to the supplier.
        Assert.Equal(cashBefore, await context.CashEvidenceAsync());
        Assert.Equal(originalBefore, await context.CreditEvidenceAsync(original));
        Assert.Equal(0m, await context.BalanceAsync(original));
        Assert.Equal(0m, await context.BalanceAsync(successor));
        Assert.Equal(8m, await context.BalanceAsync(settlement));
        Assert.Equal(-82m, await context.BankBalanceAsync());
        Assert.Equal(90m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(group, await context.Bills.ScalarAsync<Guid>($"SELECT GroupId FROM Purchasing.SupplierCredits WHERE Id='{successor}'"));
        Assert.Equal(group, await context.Bills.ScalarAsync<Guid>($"SELECT GroupId FROM Purchasing.SupplierCashSettlementReleases WHERE SettlementId='{settlement}'"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(DISTINCT RecordedAtUtc) FROM Purchasing.SupplierItemMovements WHERE GroupId='{group}'"));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Accounting.JournalLines line JOIN Accounting.JournalEntries journal ON journal.TenantId=line.TenantId AND journal.Id=line.JournalId WHERE line.AccountPurpose IN('Bank','Cash') AND journal.Id IN(SELECT JournalId FROM Purchasing.SupplierSourceCorrectionMembers WHERE GroupId='{group}' AND JournalId IS NOT NULL)"));
    }

    [Fact]
    public async Task AllocatedCreditCorrectionReleasesAndExplicitlyReapplies()
    {
        // GIVEN a credit of thirty with twenty applied to its bill.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var bill = await context.ExpenseBillAsync("100");
        var credit = await context.CreditAsync(bill, "30"); var original = Guid.Parse(credit["sourceId"]!.GetValue<string>());
        await context.Payments.Allocation.ApplyAsync(await context.Payments.Allocation.CommandAsync(original, bill, "20"));
        var replacement = await context.CreditCommandAsync(bill, "25", "2026-09-20");
        var successor = Guid.Parse(replacement["creditId"]!.GetValue<string>());
        var command = await context.CorrectionCommandAsync("Credit", original, replacement);
        command["reapplications"]!.AsArray().Add(new JsonObject { ["fundingItemId"] = successor.ToString(), ["debtItemId"] = bill.ToString(), ["amount"] = "15" });
        // WHEN inverse and explicit replacement application commit as one source correction.
        await context.CaptureCorrectionPlanAsync(command); await context.CorrectAsync("Credit", command);
        // THEN gross capacity is replaced once and only fifteen remains applied.
        Assert.Equal(0m, await context.BalanceAsync(original));
        Assert.Equal(10m, await context.BalanceAsync(successor));
        Assert.Equal(85m, await context.BalanceAsync(bill));
        Assert.Equal(75m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(0m, await context.BankBalanceAsync());
    }

    [Fact]
    public async Task MissingReplacementCannotSilentlyMeanReversal()
    {
        // GIVEN an authentic unconsumed credit and an incomplete correction intent.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var credit = await context.CreditAsync(await context.ExpenseBillAsync("100"), "18");
        var original = Guid.Parse(credit["sourceId"]!.GetValue<string>());
        var command = await context.CorrectionCommandAsync("Credit", original, null); command.Remove("replacement");
        // WHEN the replacement decision is omitted THEN preview rejects instead of guessing a reversal.
        Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Credit", command))).Number);
        command["replacement"] = null;
        // WHEN reversal is explicit THEN the original source closes without cash or a successor.
        await context.CaptureCorrectionPlanAsync(command); await context.CorrectAsync("Credit", command);
        Assert.Equal(0m, await context.BalanceAsync(original));
        Assert.Equal(100m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(0m, await context.BankBalanceAsync());
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCredits"));
    }

    [Theory]
    [InlineData("2026-09-18", "20", "80", "2026-09-21")]
    [InlineData("2026-09-18", "20", "80", "2026-09-16")]
    [InlineData("2026-09-18", "100", "0", "2026-09-21")]
    [InlineData("2026-09-22", "20", "80", "2026-09-21")]
    [InlineData("2026-09-22", "100", "0", "2026-09-21")]
    public async Task PrepaymentCreditCorrectionPreservesMatchedRecognitionAndQuantities(string date, string originalAmount, string recognizedAmount, string recognitionDate)
    {
        // GIVEN a genuine invoice of one hundred recognized net of its prepayment credit, including zero-only recognition.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var bill = await context.PrepaymentBillAsync("100");
        var source = await context.CreditAsync(bill.BillId, originalAmount); var original = Guid.Parse(source["sourceId"]!.GetValue<string>());
        await context.RecognizeAsync(bill.UnitId, recognizedAmount, recognitionDate);
        var sourceBefore = await context.Bills.ScalarAsync<string>($"SELECT (SELECT * FROM Purchasing.RecognitionSideEvents WHERE UnitId='{bill.UnitId}' ORDER BY Id FOR JSON PATH)");
        var replacement = await context.CreditCommandAsync(bill.BillId, "10", date);
        var command = await context.CorrectionCommandAsync("Credit", original, replacement, date);
        // WHEN correction occurs before or after the recorded match THEN its monetary dependency changes as one closure.
        await context.CaptureCorrectionPlanAsync(command); await context.CorrectAsync("Credit", command);
        Assert.Equal(90m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(0m, await context.Bills.Recognition.BalanceAsync("Prepayment"));
        Assert.Equal(-100m, await context.Bills.Recognition.BalanceAsync("SupplierPayable"));
        Assert.Equal(1m, await context.Bills.ScalarAsync<decimal>($"SELECT Quantity FROM Purchasing.RecognitionUnits WHERE Id='{bill.UnitId}'"));
        Assert.Equal(sourceBefore, await context.Bills.ScalarAsync<string>($"SELECT (SELECT * FROM Purchasing.RecognitionSideEvents WHERE UnitId='{bill.UnitId}' ORDER BY Id FOR JSON PATH)"));
    }
}
