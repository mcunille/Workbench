// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCreditPostingTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task CreditCreatesReceivableWithoutCashOrBillMutation()
    {
        // GIVEN an authentic reviewed and recognized expense bill of one hundred.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var bill = await context.ExpenseBillAsync("100");
        var billBefore = await context.ReadOriginalBillEvidenceAsync(bill);
        Assert.Equal(100m, await context.BalanceAsync(bill));
        JsonObject? posted = null;

        // WHEN a supplier credit reduces that cost by eighteen through the source-owned command.
        var postingError = await Record.ExceptionAsync(async () => posted = await context.CreditAsync(bill, "18"));

        // THEN the credit exists without reducing AP or changing recorded cash or the original bill.
        Assert.Null(postingError);
        var credit = Guid.Parse(posted!["sourceId"]!.GetValue<string>());
        Assert.Equal(18m, await context.BalanceAsync(credit));
        Assert.Equal(100m, await context.BalanceAsync(bill));
        Assert.Equal(0m, await context.BankBalanceAsync());
        Assert.Equal(billBefore, await context.ReadOriginalBillEvidenceAsync(bill));
        Assert.Equal(18m, await context.Bills.ScalarAsync<decimal>($"SELECT SUM(Debit-Credit) FROM Accounting.JournalLines WHERE AccountId='{context.CreditAccount}'"));
        var expense = context.Bills.Recognition.Accounts["Expense"];
        Assert.Equal(82m, await context.Bills.ScalarAsync<decimal>($"SELECT SUM(Debit-Credit) FROM Accounting.JournalLines WHERE AccountId='{expense}'"));
        Assert.Equal(0m, await context.Bills.ScalarAsync<decimal>("SELECT SUM(Debit-Credit) FROM Accounting.JournalLines"));
    }
}
