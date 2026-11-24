// Copyright (c) 2026 The White Stag Collection.
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCreditReportTests(SqlServerFixture server)
{
    [Fact]
    public async Task GenuineBillSuccessorAndOriginalInverseReconcile()
    {
        // GIVEN an authentic posted bill and a separate reviewed successor with a new amount.
        await using var context = await SupplierCreditTestContext.OpenAsync(server);
        await context.EnableBillCorrectionsAsync();
        var original = await context.InvoiceOnlyBillAsync("100");
        var replacement = await context.BillReplacementAsync(original, "75");
        var correction = await context.CorrectionCommandAsync("Bill", original, replacement);
        await context.CaptureBillCorrectionPlanAsync(correction);
        await context.CorrectAsync("Bill", correction);

        // WHEN reading reconciliation THEN both the old inverse and new payable have their actual owners.
        var report = await SupplierReconciliationTests.Read(context.Payments);
        Assert.True(report.IsComplete);
        var payable = Assert.Single(report.Controls.Items);
        Assert.Equal(("Payable", "75.00", "75.00", "0.00"),
            (payable.ControlFamily, payable.JournalAmount, payable.SubledgerAmount, payable.Difference));
    }
}
