// Copyright (c) 2026 The White Stag Collection.
using System.Globalization;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCashReportTests(SupplierCashClearingFixture clearing) : IClassFixture<SupplierCashClearingFixture>
{
    [Fact]
    public async Task CreditRefundAndRepaymentControlsHaveExactAuthenticOwners()
    {
        // GIVEN genuine credit/refund/correction/allocation history, an actual repayment, and a partly returned purchase deposit.
        var prepared = await clearing.OpenAsync(); await using var context = prepared.Context; var history = prepared.History;
        await context.RepaymentAsync([(history.Clearing, "3")]);
        var deposit = await context.Payments.CommandAsync("20", "2026-09-10"); await context.Payments.RecordAsync(deposit);
        var advance = Guid.Parse(deposit["paymentId"]!.ToString()); await context.RefundAsync([(advance, "10")]);
        // WHEN all four historical controls are read under the real report boundary.
        var report = await SupplierReconciliationTests.Read(context.Payments);
        // THEN every journal/item owner is complete, including zero credit net, and each amount independently matches retained SQL facts.
        Assert.True(report.IsComplete); Assert.Equal(0, report.UnresolvedTenantControlCount);
        Assert.Equal(new[] { "Advance", "CreditReceivable", "Payable", "RefundClearing" }, report.Controls.Items.Select(c => c.ControlFamily).Order().ToArray());
        Assert.Equal(("100.00", "10.00", "0.00", "5.00", "95.00"),
            (report.Controls.WholeFilterTotals.Payable, report.Controls.WholeFilterTotals.Advance, report.Controls.WholeFilterTotals.CreditReceivable,
                report.Controls.WholeFilterTotals.RefundClearing, report.Controls.WholeFilterTotals.NetSupplierPosition));
        foreach (var control in report.Controls.Items)
        {
            Assert.True(control.IsComplete); Assert.Equal("0.00", control.Difference); Assert.NotNull(control.AccountId);
            var journal = await context.Bills.ScalarAsync<decimal>($"""
                SELECT COALESCE(SUM(CASE WHEN AccountType='Asset' THEN Debit-Credit ELSE Credit-Debit END),0)
                FROM Accounting.JournalLines WHERE AccountId='{control.AccountId}' AND AccountPurpose='Supplier{control.ControlFamily}'
                """);
            var item = await context.Bills.ScalarAsync<decimal>($"""
                SELECT COALESCE(SUM(movement.Amount),0) FROM Purchasing.SupplierItemMovements movement
                JOIN Purchasing.SupplierOpenItems item ON item.TenantId=movement.TenantId AND item.Id=movement.ItemId
                WHERE item.Kind='{control.ControlFamily}'
                """);
            Assert.Equal(journal, decimal.Parse(control.JournalAmount, CultureInfo.InvariantCulture));
            Assert.Equal(item, decimal.Parse(control.SubledgerAmount, CultureInfo.InvariantCulture));
        }
        Assert.Equal(5m, await context.BankBalanceAsync()); Assert.Equal(90m, await context.Bills.Recognition.BalanceAsync("Expense"));
    }

    [Theory]
    [InlineData("offsetMovements")]
    [InlineData("duplicateAttribution")]
    [InlineData("fundingLine")]
    public async Task CashOwnershipCorruptionCannotHideBehindUnchangedControlTotals(string corruption)
    {
        // GIVEN two authentic five-dollar refunds of one genuine twenty-dollar purchase advance, with a complete source graph.
        var prepared = await clearing.OpenAsync(); await using var context = prepared.Context;
        var deposit = await context.Payments.CommandAsync("20", "2026-09-10"); await context.Payments.RecordAsync(deposit);
        var advance = Guid.Parse(deposit["paymentId"]!.ToString());
        var first = await context.RefundAsync([(advance, "5")]); var second = await context.RefundAsync([(advance, "5")]);
        var firstId = Guid.Parse(first["settlementIds"]![0]!.ToString()); var secondId = Guid.Parse(second["settlementIds"]![0]!.ToString());
        var valid = await SupplierReconciliationTests.Read(context.Payments); Assert.True(valid.IsComplete);
        // WHEN a restored clone has corrupted cash evidence without changing the supplier control totals.
        if (corruption == "duplicateAttribution")
            await context.Bills.AdminAsync($"""
                INSERT Purchasing.SupplierControlAttributions(TenantId,Id,GroupId,MovementId,JournalId,Ordinal,AccountId,AccountVersion,AccountPurpose,Amount)
                SELECT original.TenantId,NEWID(),original.GroupId,original.MovementId,other.JournalId,other.JournalOrdinal,
                    original.AccountId,original.AccountVersion,original.AccountPurpose,CASE settlement.Id WHEN '{firstId}' THEN 5 ELSE -5 END
                FROM Purchasing.SupplierControlAttributions original
                JOIN Purchasing.SupplierCashSettlements settlement ON settlement.TenantId=original.TenantId AND settlement.MovementId=original.MovementId
                JOIN Purchasing.SupplierCashSettlements other ON other.TenantId=settlement.TenantId AND
                    other.Id=CASE settlement.Id WHEN '{firstId}' THEN '{secondId}' ELSE '{firstId}' END
                WHERE settlement.Id IN('{firstId}','{secondId}')
                """);
        else if (corruption == "offsetMovements")
            await context.Bills.AdminAsync($"""
                UPDATE movement SET Amount=CASE settlement.Id WHEN '{firstId}' THEN -4 ELSE -6 END
                FROM Purchasing.SupplierItemMovements movement JOIN Purchasing.SupplierCashSettlements settlement
                    ON settlement.TenantId=movement.TenantId AND settlement.MovementId=movement.Id
                WHERE settlement.Id IN('{firstId}','{secondId}')
                """);
        else
            await context.Bills.AdminAsync($"""
                UPDATE line SET Debit=Debit+1 FROM Accounting.JournalLines line
                JOIN Purchasing.SupplierCashSettlements settlement ON settlement.TenantId=line.TenantId AND settlement.JournalId=line.JournalId
                WHERE settlement.Id='{firstId}' AND line.AccountPurpose='Bank'
                """);
        // THEN equal GL/item totals cannot turn incomplete or duplicated historical ownership into a successful reconciliation.
        var corrupt = await SupplierReconciliationTests.Read(context.Payments);
        Assert.False(corrupt.IsComplete);
        Assert.Equal(valid.Controls.WholeFilterTotals, corrupt.Controls.WholeFilterTotals);
        Assert.All(corrupt.Controls.Items, control => Assert.Equal("0.00", control.Difference));
        Assert.Contains(corrupt.Controls.Items, control => control.InvalidSourceEvidenceCount > 0 || control.DuplicateAttributionCount > 0);
    }
}
