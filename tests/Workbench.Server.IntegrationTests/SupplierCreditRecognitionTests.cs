// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCreditRecognitionTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task CreditBeforeRecognitionReleasesOnlyNetPrepayment()
    {
        // GIVEN an invoice-first purchase of one hundred and a cost reduction of twenty.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var source = await context.PrepaymentBillAsync("100");
        await context.CreditAsync(source.BillId, "20");
        // WHEN the whole original unit is recognized for its remaining eighty.
        await context.RecognizeAsync(source.UnitId, "80");
        // THEN monetary prepayment clears once and quantity/source evidence remains gross.
        Assert.Equal(0m, await context.Bills.Recognition.BalanceAsync("Prepayment"));
        Assert.Equal(80m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(-100m, await context.Bills.Recognition.BalanceAsync("SupplierPayable"));
        Assert.Equal(20m, await context.Bills.ScalarAsync<decimal>($"SELECT SUM(Debit-Credit) FROM Accounting.JournalLines WHERE AccountId='{context.CreditAccount}'"));
        Assert.Equal(100m, await context.Bills.ScalarAsync<decimal>($"SELECT SourceAmount FROM Purchasing.RecognitionSideEvents WHERE Id='{source.InvoiceEventId}'"));
        Assert.Equal(1m, await context.Bills.ScalarAsync<decimal>($"SELECT Quantity FROM Purchasing.RecognitionUnits WHERE Id='{source.UnitId}'"));
        Assert.Equal(2, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.RecognitionSideEvents WHERE UnitId='{source.UnitId}' AND SourceQuantity=1"));
    }

    [Fact]
    public async Task BackdatedCreditCannotDoubleReleaseAlreadyMatchedPrepayment()
    {
        // GIVEN gross prepayment already consumed by a later recognition.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var source = await context.PrepaymentBillAsync("100");
        await context.RecognizeAsync(source.UnitId, "100");
        var journals = await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.JournalEntries");
        // WHEN a subsequently recorded credit predates that consumption.
        var error = await Assert.ThrowsAsync<SqlException>(() => context.CreditAsync(source.BillId, "20"));
        // THEN the later invalid position conflicts without creating financial history.
        Assert.Equal(51009, error.Number);
        Assert.Equal(journals, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Accounting.JournalEntries"));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCredits"));
        Assert.Equal(0m, await context.Bills.Recognition.BalanceAsync("Prepayment"));
    }
}
