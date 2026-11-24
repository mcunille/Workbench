// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCashCorrectionAuthorityTests(SqlServerFixture sqlServer, SupplierCashClearingFixture clearing) : IClassFixture<SupplierCashClearingFixture>
{
    private async Task<(SupplierCreditTestContext Context, Guid Item)> SourceTargetAsync(string kind)
    {
        if (kind == "RefundRepayment")
        {
            var prepared = await clearing.OpenAsync(); return (prepared.Context, prepared.History.Clearing);
        }
        var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        try
        {
            await context.EnableCashCorrectionsAsync();
            var credit = await context.CreditAsync(await context.ExpenseBillAsync("100"), "18");
            return (context, Guid.Parse(credit["sourceId"]!.ToString()));
        }
        catch { await context.DisposeAsync(); throw; }
    }

    [Theory]
    [InlineData("Refund")]
    [InlineData("RefundRepayment")]
    public async Task EachCashCorrectionRequiresItsOwnCurrentAuthoritiesBeforePreviewAndReplay(string kind)
    {
        // GIVEN an authentic incoming or outgoing cash record and an explicit fresh successor of the same family.
        var prepared = await SourceTargetAsync(kind); await using var context = prepared.Context; var item = prepared.Item;
        var source = kind == "Refund" ? await context.RefundAsync([(item, "8")]) : await context.RepaymentAsync([(item, "8")]);
        var original = Guid.Parse(source["sourceId"]!.ToString());
        var replacement = kind == "Refund" ? await context.RefundCommandAsync([(item, "3")], "2026-09-22")
            : await context.RepaymentCommandAsync([(item, "3")], "2026-09-22");
        var command = await context.CorrectionCommandAsync(kind, original, replacement, "2026-09-22");
        var permissions = new[] { $"Supplier{kind}sCorrect", $"Supplier{kind}sRecord" };
        foreach (var permission in permissions)
        {
            // WHEN one required current family permission is lost THEN preview denies before creating financial history.
            await context.Bills.AdminAsync($"DELETE [Identity].RoleClaims WHERE ClaimType='workbench/permission' AND ClaimValue='{permission}'");
            var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);
            Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync(kind, command))).Number);
            Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
            await context.Bills.AdminAsync($"INSERT [Identity].RoleClaims(TenantId,RoleId,ClaimType,ClaimValue) SELECT TenantId,RoleId,N'workbench/permission',N'{permission}' FROM Administration.AccountingRoles WHERE Kind='Administrator'");
        }
        await context.CaptureCashCorrectionPlanAsync(kind, command); var request = Guid.NewGuid();
        var result = await context.CorrectAsync(kind, command, request);
        Assert.Equal(result.ToJsonString(), (await context.CorrectAsync(kind, command, request)).ToJsonString());
        foreach (var permission in permissions)
        {
            // WHEN exact replay follows permission loss THEN an old success receipt cannot grant current authority.
            await context.Bills.AdminAsync($"DELETE [Identity].RoleClaims WHERE ClaimType='workbench/permission' AND ClaimValue='{permission}'");
            var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);
            Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync(kind, command, request))).Number);
            Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
            await context.Bills.AdminAsync($"INSERT [Identity].RoleClaims(TenantId,RoleId,ClaimType,ClaimValue) SELECT TenantId,RoleId,N'workbench/permission',N'{permission}' FROM Administration.AccountingRoles WHERE Kind='Administrator'");
        }
    }

    [Fact]
    public async Task ActualRepaymentDoesNotBorrowAllocationOrOtherSourceAuthority()
    {
        // GIVEN real clearing debt and an actor retaining only the outgoing-recording family among supplier write permissions.
        var prepared = await clearing.OpenAsync(); await using var context = prepared.Context; var history = prepared.History;
        var command = await context.RepaymentCommandAsync([(history.Clearing, "8")]); var request = Guid.NewGuid();
        await context.Bills.AdminAsync("DELETE [Identity].RoleClaims WHERE ClaimType='workbench/permission' AND ClaimValue IN('SupplierBillsManage','SupplierBillsPost','SupplierBillsCorrect','SupplierPaymentsRecord','SupplierPaymentsCorrect','SupplierCreditsPost','SupplierCreditsCorrect','SupplierRefundsRecord','SupplierRefundsCorrect','SupplierRefundRepaymentsCorrect','SupplierAllocationsManage')");
        // WHEN the supplier is actually repaid THEN no unrelated write permission is required.
        var result = await context.Bills.ExecuteAsync("RecordSupplierRefundRepayment", request, command);
        Assert.Equal(0m, await context.BalanceAsync(history.Clearing)); Assert.Equal(10m, await context.BankBalanceAsync());
        Assert.Equal(result.ToJsonString(), (await context.Bills.ExecuteAsync("RecordSupplierRefundRepayment", request, command)).ToJsonString());
        // WHEN recording authority is subsequently removed THEN exact replay is denied with financial state unchanged.
        await context.Bills.AdminAsync("DELETE [Identity].RoleClaims WHERE ClaimType='workbench/permission' AND ClaimValue='SupplierRefundRepaymentsRecord'");
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => context.Bills.ExecuteAsync("RecordSupplierRefundRepayment", request, command))).Number);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
    }

    [Theory]
    [InlineData("Refund", 0, 18)]
    [InlineData("RefundRepayment", 18, 8)]
    public async Task SourceOnlyCashReversalDoesNotRequireUnusedRecordingOrAllocationAuthority(string kind, int bank, int itemBalance)
    {
        // GIVEN a genuine cash source with no release/allocation dependency to reverse and no successor intent.
        var prepared = await SourceTargetAsync(kind); await using var context = prepared.Context; var item = prepared.Item;
        var source = kind == "Refund" ? await context.RefundAsync([(item, "8")]) : await context.RepaymentAsync([(item, "8")]);
        var original = Guid.Parse(source["sourceId"]!.ToString());
        var command = await context.CorrectionCommandAsync(kind, original, null, "2026-09-22");
        await context.Bills.AdminAsync($"DELETE [Identity].RoleClaims WHERE ClaimType='workbench/permission' AND ClaimValue IN('SupplierBillsManage','SupplierBillsPost','SupplierBillsCorrect','SupplierPaymentsRecord','SupplierPaymentsCorrect','SupplierAllocationsManage','SupplierCreditsPost','SupplierCreditsCorrect','SupplierRefundsRecord','SupplierRefundRepaymentsRecord','SupplierRefundsCorrect','SupplierRefundRepaymentsCorrect') AND ClaimValue<>'Supplier{kind}sCorrect'");
        // WHEN only its own recording error is reversed THEN its correction permission owns that exact inverse.
        await context.CaptureCashCorrectionPlanAsync(kind, command); await context.CorrectAsync(kind, command);
        Assert.Equal((decimal)bank, await context.BankBalanceAsync()); Assert.Equal((decimal)itemBalance, await context.BalanceAsync(item));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierPayments"));
        Assert.Equal(kind == "Refund" ? 0 : 1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCashSettlementReleases"));
    }

    [Theory]
    [InlineData("SupplierRefundsCorrect")]
    [InlineData("SupplierRefundRepaymentsCorrect")]
    [InlineData("SupplierRefundsRecord")]
    [InlineData("SupplierAllocationsManage")]
    public async Task MixedCashResolutionRequiresEveryCurrentFamilyBeforePreviewAndReplay(string permission)
    {
        // GIVEN a genuine refund/release/allocation chain with a later repayment and an explicit whole-source resolution.
        var prepared = await clearing.OpenAsync(); await using var context = prepared.Context; var history = prepared.History;
        var repayment = await context.RepaymentAsync([(history.Clearing, "8")]);
        var replacement = await context.RefundCommandAsync([(history.SuccessorCredit, "10")], "2026-09-22");
        var command = await context.CorrectionCommandAsync("Refund", history.Refund, replacement, "2026-09-22");
        command["sourceDecisions"]!.AsArray().Add(new JsonObject
        {
            ["sourceKind"] = "SupplierRefundRepayment",
            ["sourceId"] = repayment["sourceId"]!.DeepClone(),
            ["expectedSourceVersion"] = repayment["version"]!.DeepClone(),
            ["replacement"] = null
        });
        // No successor repayment is proposed; historical proof must not borrow its unused recording permission.
        await context.Bills.AdminAsync("DELETE [Identity].RoleClaims WHERE ClaimType='workbench/permission' AND ClaimValue='SupplierRefundRepaymentsRecord'");
        async Task Revoke() => await context.Bills.AdminAsync($"DELETE [Identity].RoleClaims WHERE ClaimType='workbench/permission' AND ClaimValue='{permission}'");
        await Revoke(); var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);
        // WHEN any actual participant loses its own authority THEN preview rejects with no financial changes.
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Refund", command))).Number);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
        await context.Bills.AdminAsync($"INSERT [Identity].RoleClaims(TenantId,RoleId,ClaimType,ClaimValue) SELECT TenantId,RoleId,N'workbench/permission',N'{permission}' FROM Administration.AccountingRoles WHERE Kind='Administrator'");
        await context.CaptureCashCorrectionPlanAsync("Refund", command); var request = Guid.NewGuid();
        var result = await context.CorrectAsync("Refund", command, request);
        Assert.Equal(result.ToJsonString(), (await context.CorrectAsync("Refund", command, request)).ToJsonString());
        await Revoke(); before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);
        // WHEN authority is lost after success THEN the exact receipt cannot authorize replay of the mixed resolution.
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync("Refund", command, request))).Number);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
        Assert.Equal(10m, await context.BankBalanceAsync()); Assert.Equal(0m, await context.BalanceAsync(history.Clearing));
    }
}
