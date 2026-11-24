// Copyright (c) 2026 The White Stag Collection.
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCorrectionPreviewContractTests(SqlServerFixture server, SupplierCashClearingFixture clearing)
    : IClassFixture<SupplierCashClearingFixture>
{
    [Fact]
    public async Task CashPreviewDoesNotRollBackItsCallerOrOpenAPeriod()
    {
        // GIVEN a valid refund correction on a date for which no period exists, inside a caller transaction.
        var prepared = await clearing.OpenAsync();
        await using var context = prepared.Context;
        var input = await context.CorrectionCommandAsync("Refund", prepared.History.Refund, null, "2026-10-01");
        await using var begin = new SqlCommand("""
            CREATE TABLE #CallerWork(Value int);
            BEGIN TRANSACTION;
            INSERT #CallerWork VALUES (17);
            """, context.Bills.Journal.Connection);
        await begin.ExecuteNonQueryAsync();
        try
        {
            // WHEN the successful preview returns THEN it preserves the transaction and leaves October unmaterialized.
            var plan = await context.PreviewCorrectionAsync("Refund", input);
            await using var check = new SqlCommand("SELECT Value FROM #CallerWork WHERE @@TRANCOUNT=1;", context.Bills.Journal.Connection);
            Assert.Equal(17, (int)(await check.ExecuteScalarAsync())!);
            Assert.Equal(0m, RemainingBalance(plan, prepared.History.Clearing));
            Assert.Equal(10m, RemainingBalance(plan, prepared.History.SuccessorCredit));
            Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.Periods WHERE PeriodStart='2026-10-01'"));
        }
        finally
        {
            await using var rollback = new SqlCommand("IF @@TRANCOUNT>0 ROLLBACK;", context.Bills.Journal.Connection);
            await rollback.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task CashCorrectionCannotReverseAndReplaceOnDifferentPostingDates()
    {
        // GIVEN a recorded refund and a feasible successor one posting day after its proposal's inverse.
        var prepared = await clearing.OpenAsync();
        await using var context = prepared.Context;
        var replacement = await context.RefundCommandAsync([(prepared.History.SuccessorCredit, "10")], "2026-10-01");
        var input = await context.CorrectionCommandAsync("Refund", prepared.History.Refund, replacement, "2026-09-30");

        // WHEN previewing THEN the mismatched date rejects before any financial changes.
        Assert.Equal(51004, (await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Refund", input))).Number);
        Assert.Equal(18m, await context.BankBalanceAsync());
    }

    [Fact]
    public async Task CreditPreviewReportsTheNewClearingBalanceAndRequiresOnePostingDate()
    {
        // GIVEN an actual refund of a credit of 18 and a proposed correction to 10 with explicit reapplication.
        await using var context = await SupplierCreditTestContext.OpenAsync(server);
        var bill = await context.ExpenseBillAsync("100");
        var credit = Guid.Parse((await context.CreditAsync(bill, "18"))["sourceId"]!.ToString());
        var refund = await context.RefundAsync([(credit, "18")]);
        var clearingId = Guid.Parse(refund["settlementIds"]![0]!.ToString());
        var replacement = await context.CreditCommandAsync(bill, "10", "2026-09-20");
        var command = await context.CorrectionCommandAsync("Credit", credit, replacement);
        command["reapplications"]!.AsArray().Add(new JsonObject
        {
            ["fundingItemId"] = replacement["creditId"]!.DeepClone(),
            ["debtItemId"] = clearingId.ToString(),
            ["amount"] = "10"
        });

        // WHEN previewing the complete group THEN the prepared clearing position is 8, without materializing a liability.
        var plan = await context.PreviewCorrectionAsync("Credit", command);
        Assert.Equal(8m, RemainingBalance(plan, clearingId));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.SupplierOpenItems WHERE Id='{clearingId}'"));

        // AND a different replacement date cannot expose only part of the group at a posting cutoff.
        command["replacement"]!["postingDate"] = "2026-09-21";
        Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Credit", command))).Number);
    }

    internal static decimal RemainingBalance(JsonObject plan, Guid itemId)
    {
        var position = Assert.Single(plan["remainingPositions"]!.AsArray(),
            row => Guid.Parse(row!["itemId"]!.GetValue<string>()) == itemId);
        return decimal.Parse(position!["balance"]!.GetValue<string>(), CultureInfo.InvariantCulture);
    }
}
