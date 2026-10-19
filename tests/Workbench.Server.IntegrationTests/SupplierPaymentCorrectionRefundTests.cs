// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierPaymentCorrectionRefundTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task PaymentCorrectionAfterAdvanceRefundCannotIgnoreCashDependency()
    {
        // GIVEN an actual deposit followed by an actual refund of part of its unused advance.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        await context.Bills.AdminAsync("""
            GRANT EXECUTE ON Purchasing.PreviewSupplierPaymentCorrection TO workbench_web;
            GRANT EXECUTE ON Purchasing.CorrectSupplierPayment TO workbench_web;
            INSERT [Identity].RoleClaims(TenantId,RoleId,ClaimType,ClaimValue)
              SELECT TenantId,RoleId,N'workbench/permission',N'SupplierPaymentsCorrect'
              FROM Administration.AccountingRoles WHERE Kind='Administrator';
            """);
        var payment = await context.Payments.CommandAsync("100"); await context.Payments.RecordAsync(payment);
        var id = Guid.Parse(payment["paymentId"]!.ToString());
        var originalPlan = await SupplierCorrectionFixture.CorrectionAsync(context.Payments, id);
        await context.RefundAsync([(id, "30")]);
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);
        var cash = await context.CashEvidenceAsync();
        // WHEN preview observes the current payment version without an explicit refund resolution.
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() =>
            SupplierCorrectionFixture.CorrectionAsync(context.Payments, id))).Number);
        // AND execution retries the earlier plan THEN neither cash source may be silently reversed.
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() =>
            context.Bills.ExecuteAsync("CorrectSupplierPayment", Guid.NewGuid(), originalPlan))).Number);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
        Assert.Equal(cash, await context.CashEvidenceAsync());
        Assert.Equal(70m, await context.BalanceAsync(id));
        Assert.Equal(-70m, await context.BankBalanceAsync());
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierPaymentCorrections"));
    }
}
