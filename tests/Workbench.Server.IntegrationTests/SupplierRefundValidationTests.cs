// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierRefundValidationTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task ReceiptReplayIsExactAndCannotChangeSettlementTargets()
    {
        // GIVEN two genuine advances and an actual receipt settling the first.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var first = await context.Payments.CommandAsync("100"); await context.Payments.RecordAsync(first);
        var second = await context.Payments.CommandAsync("100"); await context.Payments.RecordAsync(second);
        var firstId = Guid.Parse(first["paymentId"]!.GetValue<string>());
        var secondId = Guid.Parse(second["paymentId"]!.GetValue<string>());
        var command = await context.RefundCommandAsync([(firstId, "40")]);
        var request = Guid.NewGuid();
        var posted = await context.Bills.ExecuteAsync("RecordSupplierRefund", request, command);
        // WHEN the same request is retried THEN its exact committed result is returned.
        Assert.Equal(posted.ToJsonString(), (await context.Bills.ExecuteAsync("RecordSupplierRefund", request, command)).ToJsonString());
        // WHEN a different target is substituted THEN neither request nor source identity can be reused.
        command["settlements"]![0]!["itemId"] = secondId.ToString();
        command["settlements"]![0]!["expectedItemVersion"] = await context.Payments.Allocation.VersionAsync(secondId);
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync("RecordSupplierRefund", request, command))).Number);
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync("RecordSupplierRefund", Guid.NewGuid(), command))).Number);
        Assert.Equal(60m, await context.BalanceAsync(firstId));
        Assert.Equal(100m, await context.BalanceAsync(secondId));
        Assert.Equal(-160m, await context.BankBalanceAsync());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("40.001")]
    [InlineData("40.00001")]
    [InlineData("1000000000000000000000000")]
    [InlineData("41")]
    public async Task InvalidGrossReceiptCannotCreatePartialMoney(string amount)
    {
        // GIVEN a genuine advance and an explicit forty-dollar settlement.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var deposit = await context.Payments.CommandAsync("100"); await context.Payments.RecordAsync(deposit);
        var advance = Guid.Parse(deposit["paymentId"]!.GetValue<string>());
        var command = await context.RefundCommandAsync([(advance, "40")]); command["amount"] = amount;
        var journals = await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.JournalEntries");
        // WHEN invalid precision, range, sign, or gross attribution is submitted.
        var error = await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync("RecordSupplierRefund", Guid.NewGuid(), command));
        // THEN admission fails before any cash source or ledger effect exists.
        Assert.Equal(51000, error.Number);
        Assert.Equal(100m, await context.BalanceAsync(advance));
        Assert.Equal(journals, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.JournalEntries"));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCashSources"));
    }

    [Theory]
    [InlineData("excess", 51009)]
    [InlineData("predates", 51004)]
    [InlineData("stale", 51009)]
    public async Task RefundRequiresAvailableAssetAndCurrentVersion(string change, int expected)
    {
        // GIVEN a real deposit and a complete refund envelope.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var deposit = await context.Payments.CommandAsync("100", "2026-09-10"); await context.Payments.RecordAsync(deposit);
        var advance = Guid.Parse(deposit["paymentId"]!.GetValue<string>());
        var command = await context.RefundCommandAsync([(advance, change == "excess" ? "101" : "40")], change == "predates" ? "2026-09-09" : "2026-09-18");
        if (change == "stale") await context.RefundAsync([(advance, "10")]);
        var before = await context.BalanceAsync(advance);
        // WHEN capacity, source date, or the captured item version is invalid THEN money is unchanged.
        var error = await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync("RecordSupplierRefund", Guid.NewGuid(), command));
        Assert.Equal(expected, error.Number);
        Assert.Equal(before, await context.BalanceAsync(advance));
    }

    [Fact]
    public async Task BackdatedRefundCannotConsumeFundingNeededAtLaterBoundary()
    {
        // GIVEN an advance partly committed to a later bill application.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var deposit = await context.Payments.CommandAsync("100", "2026-09-10"); await context.Payments.RecordAsync(deposit);
        var advance = Guid.Parse(deposit["paymentId"]!.GetValue<string>());
        var bill = await context.Payments.Allocation.BillAsync("100");
        await context.Payments.Allocation.ApplyAsync(await context.Payments.Allocation.CommandAsync(advance, bill, "50", "2026-09-20"));
        // WHEN a subsequently recorded cash return would invalidate that later application.
        var error = await Assert.ThrowsAsync<SqlException>(() => context.RefundAsync([(advance, "75")], "2026-09-18"));
        // THEN temporal capacity rejects without recording incoming cash.
        Assert.Equal(51009, error.Number);
        Assert.Equal(50m, await context.BalanceAsync(advance));
        Assert.Equal(-100m, await context.BankBalanceAsync());
    }

    [Theory]
    [InlineData("General")]
    [InlineData("Card")]
    [InlineData("ArchivedBank")]
    public async Task ReceiptFundingMustBeActualBankOrCash(string kind)
    {
        // GIVEN a genuine asset to settle, but an unsupported receipt funding account.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var deposit = await context.Payments.CommandAsync("100"); await context.Payments.RecordAsync(deposit);
        var account = context.Bills.Recognition.Accounts["Prepayment"];
        if (kind != "General")
        {
            var created = await context.Bills.Journal.SaveAsync(Guid.NewGuid(), "CreateAccounts",
                kind == "Card" ? """[{"code":"REFCARD","name":"Card liability","type":"Liability","purpose":"CardLiability"}]"""
                    : """[{"code":"OLDBANK","name":"Unused old bank","type":"Asset","purpose":"Bank"}]""");
            account = Guid.Parse(JsonNode.Parse(created.Ids)![0]!.GetValue<string>());
            if (kind == "ArchivedBank") await context.Bills.Journal.SaveAsync(Guid.NewGuid(), "ArchiveAccount",
                """{"isArchived":true}""", account, created.Version);
        }
        var command = await context.RefundCommandAsync([(Guid.Parse(deposit["paymentId"]!.GetValue<string>()), "40")]);
        command["fundingAccountId"] = account.ToString();
        command["expectedFundingAccountVersion"] = (await context.Bills.ScalarAsync<Guid>($"SELECT Version FROM Accounting.Accounts WHERE Id='{account}'")).ToString();
        // WHEN a general, card, or archived bank account is offered THEN it cannot record actual incoming cash.
        var error = await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync("RecordSupplierRefund", Guid.NewGuid(), command));
        Assert.Equal(51004, error.Number);
        Assert.Equal(-100m, await context.BankBalanceAsync());
    }
}
