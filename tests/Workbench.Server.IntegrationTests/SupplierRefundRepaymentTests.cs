// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierRefundRepaymentTests(SupplierCashClearingFixture clearing) : IClassFixture<SupplierCashClearingFixture>
{
    [Theory]
    [InlineData("8", 0, 10)]
    [InlineData("3", 5, 15)]
    public async Task RepaymentClearsOnlyActualLiability(string amount, int remaining, int bank)
    {
        // GIVEN a genuine eighteen-dollar receipt retained after its credit was corrected to ten.
        var prepared = await clearing.OpenAsync(); await using var context = prepared.Context; var history = prepared.History;
        var original = await context.CashSourceEvidenceAsync(history.Refund);
        var credit = await context.CreditEvidenceAsync(history.SuccessorCredit);
        // WHEN an actual outgoing repayment settles all or part of the eight-dollar liability.
        var result = await context.RepaymentAsync([(history.Clearing, amount)]);
        var source = Guid.Parse(result["sourceId"]!.ToString());
        // THEN the historical clearing is debited once and cash falls without another purchase payment or cost change.
        Assert.Equal((decimal)remaining, await context.BalanceAsync(history.Clearing));
        Assert.Equal((decimal)bank, await context.BankBalanceAsync());
        Assert.Equal(90m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierPayments"));
        Assert.Equal("SupplierRefundRepayment", await context.Bills.ScalarAsync<string>($"SELECT Kind FROM Purchasing.SupplierCashSources WHERE Id='{source}'"));
        Assert.Equal(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), await context.Bills.ScalarAsync<decimal>($"SELECT SUM(line.Debit) FROM Accounting.SourceEvents actual JOIN Accounting.JournalEntries journal ON journal.TenantId=actual.TenantId AND journal.SourceEventId=actual.Id JOIN Accounting.JournalLines line ON line.TenantId=journal.TenantId AND line.JournalId=journal.Id WHERE actual.SourceId='{source}' AND line.AccountPurpose='SupplierRefundClearing'"));
        Assert.Equal(original, await context.CashSourceEvidenceAsync(history.Refund));
        Assert.Equal(credit, await context.CreditEvidenceAsync(history.SuccessorCredit));
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
        // WHEN another outgoing record exceeds the remaining real debt THEN no partial source or cash survives.
        var excessive = await context.RepaymentCommandAsync([(history.Clearing, (remaining + 1).ToString())]);
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);
        await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync("RecordSupplierRefundRepayment", Guid.NewGuid(), excessive));
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
    }
}
