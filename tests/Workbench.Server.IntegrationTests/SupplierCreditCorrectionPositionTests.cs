// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed partial class SupplierCreditCorrectionTests
{
    [Fact]
    public async Task ReplacementClaimRecognitionAfterPreviewChangesTheCompleteCreditFingerprint()
    {
        // GIVEN a real credit correction preview that moves its replacement claim to another unrecognized bill.
        await using var context = await SupplierCreditTestContext.OpenAsync(Fixture);
        var firstBill = await context.ExpenseBillAsync("100"); var original = await context.CreditAsync(firstBill, "18");
        var originalId = Guid.Parse(original["sourceId"]!.ToString()); var otherBill = await context.InvoiceOnlyBillAsync("100");
        var replacement = await context.CreditCommandAsync(otherBill, "10", "2026-09-20");
        var command = await context.CorrectionCommandAsync("Credit", originalId, replacement);
        await context.CaptureCorrectionPlanAsync(command);
        var unit = await context.Bills.ScalarAsync<Guid>($"SELECT actual.UnitId FROM Purchasing.SupplierBillPostingEvents posting JOIN Purchasing.RecognitionSideEvents actual ON actual.TenantId=posting.TenantId AND actual.Id=posting.EventId WHERE posting.BillId='{otherBill}'");
        await context.RecognizeAsync(unit, "100", "2026-09-19");
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);
        // WHEN genuine recognition changes the same-capacity target from prepayment to expense THEN the old fingerprint conflicts.
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync("Credit", command))).Number);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
        // AND refreshed source evidence permits the real credit owner to complete with the correct historical cost account.
        await context.CaptureCorrectionPlanAsync(command); await context.CorrectAsync("Credit", command);
        Assert.Equal(190m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(0m, await context.Bills.Recognition.BalanceAsync("Prepayment"));
        Assert.Equal(0m, await context.BalanceAsync(originalId));
        Assert.Equal(10m, await context.BalanceAsync(Guid.Parse(replacement["creditId"]!.ToString())));
    }
}
