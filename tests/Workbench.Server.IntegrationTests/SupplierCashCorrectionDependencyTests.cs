// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCashCorrectionDependencyTests(SupplierCashClearingFixture clearing) : IClassFixture<SupplierCashClearingFixture>
{
    [Fact]
    public async Task RefundCorrectionDiscoversAlreadyRepaidClearing()
    {
        // GIVEN a real eighteen-dollar refund, credit correction/reapplication and later eight-dollar repayment.
        var prepared = await clearing.OpenAsync(); await using var context = prepared.Context; var history = prepared.History;
        var repayment = await context.RepaymentAsync([(history.Clearing, "8")]); var repaymentId = Guid.Parse(repayment["sourceId"]!.ToString());
        var refundEvidence = await context.CashSourceEvidenceAsync(history.Refund);
        var repaymentEvidence = await context.CashSourceEvidenceAsync(repaymentId);
        var replacement = await context.RefundCommandAsync([(history.SuccessorCredit, "10")], "2026-09-22");
        var command = await context.CorrectionCommandAsync("Refund", history.Refund, replacement, "2026-09-22");
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);
        // WHEN the later cash source decision is omitted THEN the complete closure rejects without partial history.
        var omitted = await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Refund", command));
        Assert.Contains(omitted.Number, new[] { 51004, 51009 });
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
        // WHEN repayment reversal is explicit THEN the source owner releases actual clearing dependencies together.
        command["sourceDecisions"]!.AsArray().Add(new JsonObject
        {
            ["sourceKind"] = "SupplierRefundRepayment",
            ["sourceId"] = repaymentId.ToString(),
            ["expectedSourceVersion"] = repayment["version"]!.DeepClone(),
            ["replacement"] = null
        });
        await context.CaptureCashCorrectionPlanAsync("Refund", command); await context.CorrectAsync("Refund", command);
        // THEN neither old source changes, the old clearing closes and the sole surviving ten-dollar receipt consumes real credit.
        Assert.Equal(0m, await context.BalanceAsync(history.Clearing)); Assert.Equal(0m, await context.BalanceAsync(history.OriginalCredit));
        Assert.Equal(0m, await context.BalanceAsync(history.SuccessorCredit)); Assert.Equal(10m, await context.BankBalanceAsync());
        Assert.Equal(90m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(refundEvidence, await context.CashSourceEvidenceAsync(history.Refund));
        Assert.Equal(repaymentEvidence, await context.CashSourceEvidenceAsync(repaymentId));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierPayments"));
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
    }
}
