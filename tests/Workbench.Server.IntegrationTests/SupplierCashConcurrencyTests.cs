// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCashConcurrencyTests(SqlServerFixture sqlServer, SupplierCashClearingFixture clearing) : IClassFixture<SupplierCashClearingFixture>
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentRefundsCannotReturnMoreThanTheRealAdvance(bool firstWins)
    {
        // GIVEN one genuine hundred-dollar advance and two independent sixty-dollar refund commands.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var deposit = await context.Payments.CommandAsync("100", "2026-09-10"); await context.Payments.RecordAsync(deposit);
        var advance = Guid.Parse(deposit["paymentId"]!.ToString());
        var first = await context.RefundCommandAsync([(advance, "60")]);
        var second = await context.RefundCommandAsync([(advance, "60")]);
        await using var sibling = await context.Bills.Journal.OpenSiblingAsync();
        var baselineReceipts = await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierFinancialReceipts");
        var successful = 0;
        async Task Record(JsonObject command, SqlConnection connection)
        { await context.Bills.ExecuteAsync("RecordSupplierRefund", Guid.NewGuid(), command, connection); successful++; }
        // WHEN each serial order is forced on independent restricted connections and SQL observes the blocked peer.
        var error = await OrderedAsync(context, firstWins, sibling,
            () => Record(first, context.Bills.Journal.Connection), () => Record(second, sibling));
        // THEN only one source and receipt exist; forty remains refundable and no expense or credit is invented.
        Assert.Equal(51009, error?.Number); Assert.Equal(1, successful);
        Assert.Equal(40m, await context.BalanceAsync(advance)); Assert.Equal(-40m, await context.BankBalanceAsync());
        Assert.Equal(0m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCashSources"));
        Assert.Equal(baselineReceipts + 1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierFinancialReceipts"));
        Assert.Equal(Guid.Parse((firstWins ? first : second)["cashSourceId"]!.ToString()),
            await context.Bills.ScalarAsync<Guid>("SELECT Id FROM Purchasing.SupplierCashSources"));
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefundAndApplicationCannotSpendTheSameAdvance(bool refundFirst)
    {
        // GIVEN a hundred-dollar advance and bill, with refund and allocation each proposing to consume sixty.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var deposit = await context.Payments.CommandAsync("100", "2026-09-10"); await context.Payments.RecordAsync(deposit);
        var advance = Guid.Parse(deposit["paymentId"]!.ToString()); var bill = await context.ExpenseBillAsync("100");
        var refund = await context.RefundCommandAsync([(advance, "60")]);
        var apply = await context.Payments.Allocation.CommandAsync(advance, bill, "60");
        await using var sibling = await context.Bills.Journal.OpenSiblingAsync();
        async Task Refund() { await context.Bills.ExecuteAsync("RecordSupplierRefund", Guid.NewGuid(), refund); }
        async Task Apply() { await context.Bills.ExecuteAsync("ApplySupplierFunds", Guid.NewGuid(), apply, sibling); }
        // WHEN both orders overlap at the real shared Accounting lock THEN the stale consumer loses atomically.
        var error = await OrderedAsync(context, refundFirst, sibling, Refund, Apply);
        Assert.Equal(51009, error?.Number);
        Assert.Equal(40m, await context.BalanceAsync(advance)); Assert.Equal(refundFirst ? 100m : 40m, await context.BalanceAsync(bill));
        Assert.Equal(refundFirst ? -40m : -100m, await context.BankBalanceAsync());
        Assert.Equal(100m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(refundFirst ? 1 : 0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCashSources"));
        Assert.Equal(refundFirst ? 0 : 1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierApplications"));
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentRepaymentsCannotExceedActualClearing(bool firstWins)
    {
        // GIVEN an authentic eight-dollar clearing liability and two five-dollar outgoing cash commands.
        var prepared = await clearing.OpenAsync(); await using var context = prepared.Context; var history = prepared.History;
        var first = await context.RepaymentCommandAsync([(history.Clearing, "5")]);
        var second = await context.RepaymentCommandAsync([(history.Clearing, "5")]);
        await using var sibling = await context.Bills.Journal.OpenSiblingAsync();
        var successful = 0;
        async Task Record(JsonObject command, SqlConnection connection)
        { await context.Bills.ExecuteAsync("RecordSupplierRefundRepayment", Guid.NewGuid(), command, connection); successful++; }
        // WHEN SQL proves the peer blocked behind each possible winner THEN only one actual payout commits.
        var error = await OrderedAsync(context, firstWins, sibling,
            () => Record(first, context.Bills.Journal.Connection), () => Record(second, sibling));
        Assert.Equal(51009, error?.Number); Assert.Equal(1, successful);
        Assert.Equal(3m, await context.BalanceAsync(history.Clearing)); Assert.Equal(13m, await context.BankBalanceAsync());
        Assert.Equal(90m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCashSources WHERE Kind='SupplierRefundRepayment'"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierFinancialReceipts WHERE Operation='RecordSupplierRefundRepayment'"));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierPayments"));
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
    }

    private static Task<SqlException?> OrderedAsync(SupplierCreditTestContext context, bool firstWins, SqlConnection sibling, Func<Task> first, Func<Task> second)
        => PurchaseRecognitionConcurrencyTests.InOrderAsync(context.Bills.Recognition,
            firstWins ? context.Bills.Journal.Connection : sibling, firstWins ? sibling : context.Bills.Journal.Connection,
            firstWins ? first : second, firstWins ? second : first);
}
