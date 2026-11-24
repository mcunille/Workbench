// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierRefundTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task DepositReturnDoesNotCreateCreditOrExpense()
    {
        // GIVEN a real deposit of one hundred, without a bill or credit.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var deposit = await context.Payments.CommandAsync("100", "2026-09-10");
        await context.Payments.RecordAsync(deposit);
        var advance = Guid.Parse(deposit["paymentId"]!.GetValue<string>());
        // WHEN two actual receipts return forty and thirty of the unused deposit.
        await context.RefundAsync([(advance, "40")]);
        Assert.Equal(60m, await context.BalanceAsync(advance));
        Assert.Equal(-60m, await context.BankBalanceAsync());
        await context.RefundAsync([(advance, "30")]);
        // THEN the remaining advance is thirty, without creating purchase expense or a credit note.
        Assert.Equal(30m, await context.BalanceAsync(advance));
        Assert.Equal(-30m, await context.BankBalanceAsync());
        Assert.Equal(0m, await context.Bills.Recognition.BalanceAsync("SupplierPayable"));
        Assert.Equal(0m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCredits"));
        Assert.Equal(2, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCashSettlements"));
    }

    [Fact]
    public async Task PaidBillCreditAndRefundReconcile()
    {
        // GIVEN a deposit, recognized bill, and final payment totaling 306.60.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var deposit = await context.Payments.CommandAsync("100", "2026-09-10");
        await context.Payments.RecordAsync(deposit);
        var advance = Guid.Parse(deposit["paymentId"]!.GetValue<string>());
        var bill = await context.ExpenseBillAsync("306.60");
        await context.Payments.Allocation.ApplyAsync(await context.Payments.Allocation.CommandAsync(advance, bill));
        var final = await context.Payments.CommandAsync("206.60", "2026-09-16");
        await context.Payments.AllocateAsync(final, bill, "206.60"); await context.Payments.RecordAsync(final);
        var payments = await context.Bills.ScalarAsync<string>("SELECT * FROM Purchasing.SupplierPayments ORDER BY Id FOR JSON PATH");
        var credit = await context.CreditAsync(bill, "18", "2026-09-17");
        var creditId = Guid.Parse(credit["sourceId"]!.GetValue<string>());
        // WHEN the supplier actually refunds the eighteen-dollar credit.
        await context.RefundAsync([(creditId, "18")]);
        // THEN all control items clear, net cost and cash agree, and original payments remain exact.
        Assert.Equal(-288.60m, await context.BankBalanceAsync());
        Assert.Equal(288.60m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(0m, await context.BalanceAsync(bill));
        Assert.Equal(0m, await context.BalanceAsync(advance));
        Assert.Equal(0m, await context.BalanceAsync(creditId));
        Assert.Equal(payments, await context.Bills.ScalarAsync<string>("SELECT * FROM Purchasing.SupplierPayments ORDER BY Id FOR JSON PATH"));
    }

    [Fact]
    public async Task RefundRetainsTwoHistoricalVersionsOfOneControl()
    {
        // GIVEN two deposits posted on either side of an advance-account rename.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var first = await context.Payments.CommandAsync("60"); await context.Payments.RecordAsync(first);
        var account = context.Payments.Advance;
        var firstVersion = await context.Bills.ScalarAsync<Guid>($"SELECT Version FROM Accounting.Accounts WHERE Id='{account}'");
        var code = await context.Bills.ScalarAsync<string>($"SELECT Code FROM Accounting.Accounts WHERE Id='{account}'");
        var renamed = await context.Bills.Journal.SaveAsync(Guid.NewGuid(), "UpdateAccount",
            new JsonObject { ["code"] = code, ["name"] = "Renamed advance", ["description"] = "Updated label" }.ToJsonString(), account, firstVersion);
        var second = await context.Payments.CommandAsync("40"); await context.Payments.RecordAsync(second);
        // WHEN one actual receipt returns both deposits.
        var result = await context.RefundAsync([(Guid.Parse(first["paymentId"]!.GetValue<string>()), "60"),
            (Guid.Parse(second["paymentId"]!.GetValue<string>()), "40")]);
        var journal = Guid.Parse(result["journalIds"]![0]!.GetValue<string>());
        // THEN each original control snapshot survives, alongside one gross bank debit.
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Accounting.JournalLines WHERE JournalId='{journal}' AND AccountId='{account}' AND AccountVersion='{firstVersion}' AND AccountName='Supplier advances' AND Credit=60"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Accounting.JournalLines WHERE JournalId='{journal}' AND AccountId='{account}' AND AccountVersion='{renamed.Version}' AND AccountName='Renamed advance' AND Credit=40"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Accounting.JournalLines WHERE JournalId='{journal}' AND AccountId='{context.Bank}' AND Debit=100"));
        Assert.Equal(0m, await context.BankBalanceAsync());
        Assert.Equal(2, result["settlementIds"]!.AsArray().Count);
    }

    [Fact]
    public async Task OneRefundCanConsumeAdvanceAndCreditWithoutAllocationPermission()
    {
        // GIVEN genuine advance and credit assets and receipt authority alone.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var deposit = await context.Payments.CommandAsync("100"); await context.Payments.RecordAsync(deposit);
        var advance = Guid.Parse(deposit["paymentId"]!.GetValue<string>());
        var credit = await context.CreditAsync(await context.ExpenseBillAsync("100"), "18");
        var creditId = Guid.Parse(credit["sourceId"]!.GetValue<string>());
        await context.Bills.AdminAsync("DELETE [Identity].RoleClaims WHERE ClaimValue='SupplierAllocationsManage'");
        // WHEN actual cash settles ten of each asset THEN allocation authority is unnecessary.
        await context.RefundAsync([(advance, "10"), (creditId, "10")]);
        Assert.Equal(90m, await context.BalanceAsync(advance));
        Assert.Equal(8m, await context.BalanceAsync(creditId));
        Assert.Equal(-80m, await context.BankBalanceAsync());
    }
}
