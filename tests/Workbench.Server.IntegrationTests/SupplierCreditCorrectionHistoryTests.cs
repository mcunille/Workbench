// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCreditCorrectionHistoryTests(SupplierCreditCorrectionHistories histories) : IClassFixture<SupplierCreditCorrectionHistories>
{
    [Fact]
    public async Task PartialRefundAndMultipleApplicationsAreCorrectedTogether()
    {
        // GIVEN one genuine credit consumed by a receipt and two independently allocated bills, restored with fresh authority.
        var prepared = await histories.OpenAsync("partial"); await using var context = prepared.Context;
        var firstBill = Guid.Parse(prepared.Data["bill"]!.ToString()); var secondBill = Guid.Parse(prepared.Data["secondBill"]!.ToString());
        var original = Guid.Parse(prepared.Data["original"]!.ToString()); var debt = Guid.Parse(prepared.Data["debt"]!.ToString());
        var cashBefore = await context.CashEvidenceAsync();
        var replacement = await context.CreditCommandAsync(firstBill, "20", "2026-09-20");
        var successor = Guid.Parse(replacement["creditId"]!.GetValue<string>());
        var command = await context.CorrectionCommandAsync("Credit", original, replacement);
        foreach (var target in new[] { (debt, "10"), (firstBill, "6"), (secondBill, "4") })
            command["reapplications"]!.AsArray().Add(new JsonObject { ["fundingItemId"] = successor.ToString(), ["debtItemId"] = target.Item1.ToString(), ["amount"] = target.Item2 });
        // WHEN the complete closure is replaced THEN every old consumption is released exactly once.
        await context.CaptureCorrectionPlanAsync(command); await context.CorrectAsync("Credit", command);
        Assert.Equal(cashBefore, await context.CashEvidenceAsync());
        Assert.Equal(0m, await context.BalanceAsync(original)); Assert.Equal(0m, await context.BalanceAsync(successor));
        Assert.Equal(0m, await context.BalanceAsync(debt));
        Assert.Equal(94m, await context.BalanceAsync(firstBill)); Assert.Equal(96m, await context.BalanceAsync(secondBill));
        Assert.Equal(180m, await context.Bills.Recognition.BalanceAsync("Expense"));
    }

    [Fact]
    public async Task SecondGenerationCorrectionKeepsOriginalReceiptAndClearingIdentity()
    {
        // GIVEN a genuine refunded credit already corrected once, with ten applied to its eighteen-dollar clearing debt.
        var prepared = await histories.OpenAsync("second-generation"); await using var context = prepared.Context;
        var bill = Guid.Parse(prepared.Data["bill"]!.ToString()); var firstId = Guid.Parse(prepared.Data["firstId"]!.ToString());
        var debt = Guid.Parse(prepared.Data["debt"]!.ToString()); var cash = prepared.Data["cashBeforeFirst"]!.GetValue<string>();
        var second = await context.CreditCommandAsync(bill, "6", "2026-09-22"); var secondId = Guid.Parse(second["creditId"]!.GetValue<string>());
        var command = await context.CorrectionCommandAsync("Credit", firstId, second, "2026-09-22");
        command["reapplications"]!.AsArray().Add(new JsonObject { ["fundingItemId"] = secondId.ToString(), ["debtItemId"] = debt.ToString(), ["amount"] = "6" });
        // WHEN its successor is corrected again THEN the existing release is reused without another cash release.
        await context.CaptureCorrectionPlanAsync(command); await context.CorrectAsync("Credit", command);
        Assert.Equal(cash, await context.CashEvidenceAsync());
        Assert.Equal(12m, await context.BalanceAsync(debt));
        Assert.Equal(0m, await context.BalanceAsync(firstId)); Assert.Equal(0m, await context.BalanceAsync(secondId));
        Assert.Equal(94m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCashSettlementReleases"));
    }
}
