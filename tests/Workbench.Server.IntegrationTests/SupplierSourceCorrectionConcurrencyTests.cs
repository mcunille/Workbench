// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierSourceCorrectionConcurrencyTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CashCorrectionAndAllocationRespectTheApprovedItemVersion(bool correctionFirst)
    {
        // GIVEN a genuine credit with ten remaining after a recorded eight-dollar refund, and two approved consumers.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer); await context.EnableCashCorrectionsAsync();
        var bill = await context.ExpenseBillAsync("100"); var credit = await context.CreditAsync(bill, "18");
        var item = Guid.Parse(credit["sourceId"]!.ToString()); var refund = await context.RefundAsync([(item, "8")]);
        var source = Guid.Parse(refund["sourceId"]!.ToString()); var evidence = await context.CashSourceEvidenceAsync(source);
        var correction = await context.CorrectionCommandAsync("Refund", source, null, "2026-09-22");
        await context.CaptureCashCorrectionPlanAsync("Refund", correction);
        var allocation = await context.Payments.Allocation.CommandAsync(item, bill, "10", "2026-09-22");
        await using var sibling = await context.Bills.Journal.OpenSiblingAsync();
        async Task Correct() { await context.Bills.ExecuteAsync("CorrectSupplierRefund", Guid.NewGuid(), correction); }
        async Task Apply() { await context.Bills.ExecuteAsync("ApplySupplierFunds", Guid.NewGuid(), allocation, sibling); }
        // WHEN SQL observes the peer blocked in each serial order THEN only the plan matching committed item state succeeds.
        var error = await OrderedAsync(context, correctionFirst, sibling, Correct, Apply);
        Assert.Equal(51009, error?.Number);
        Assert.Equal(correctionFirst ? 18m : 0m, await context.BalanceAsync(item));
        Assert.Equal(correctionFirst ? 100m : 90m, await context.BalanceAsync(bill));
        Assert.Equal(correctionFirst ? 0m : 8m, await context.BankBalanceAsync());
        Assert.Equal(82m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(correctionFirst ? 1 : 0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierSourceCorrections"));
        Assert.Equal(correctionFirst ? 0 : 1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierApplications"));
        Assert.Equal(evidence, await context.CashSourceEvidenceAsync(source));
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreditClaimAndBillWithdrawalCannotOmitEachOther(bool creditFirst)
    {
        // GIVEN a genuine posted invoice and a complete withdrawal plan captured before a new credit claim.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer); await context.EnableBillCorrectionsAsync();
        var bill = await context.InvoiceOnlyBillAsync("100"); var original = await context.BillSourceEvidenceAsync(bill);
        var credit = await context.CreditCommandAsync(bill, "10"); var correction = await context.CorrectionCommandAsync("Bill", bill, null);
        await context.CaptureBillCorrectionPlanAsync(correction);
        await using var sibling = await context.Bills.Journal.OpenSiblingAsync();
        async Task Credit() { await context.Bills.ExecuteAsync("PostSupplierCredit", Guid.NewGuid(), credit); }
        async Task Withdraw() { await context.Bills.ExecuteAsync("CorrectSupplierBill", Guid.NewGuid(), correction, sibling); }
        // WHEN both serial orders overlap THEN a new dependent credit stales the plan, or withdrawal retires original capacity.
        var error = await OrderedAsync(context, creditFirst, sibling, Credit, Withdraw);
        Assert.NotNull(error); Assert.Contains(error.Number, new[] { 51004, 51009 });
        Assert.Equal(creditFirst ? 1 : 0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCredits"));
        Assert.Equal(creditFirst ? 0 : 1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierSourceCorrections"));
        Assert.Equal(creditFirst ? 90m : 0m, await context.Bills.Recognition.BalanceAsync("Prepayment"));
        Assert.Equal(creditFirst ? 100m : 0m, await context.BalanceAsync(bill)); Assert.Equal(0m, await context.BankBalanceAsync());
        Assert.Equal(original, await context.BillSourceEvidenceAsync(bill));
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
    }

    private static Task<SqlException?> OrderedAsync(SupplierCreditTestContext context, bool firstWins, SqlConnection sibling, Func<Task> first, Func<Task> second)
        => PurchaseRecognitionConcurrencyTests.InOrderAsync(context.Bills.Recognition,
            firstWins ? context.Bills.Journal.Connection : sibling, firstWins ? sibling : context.Bills.Journal.Connection,
            firstWins ? first : second, firstWins ? second : first);
}
