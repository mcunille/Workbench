// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierBillSourceCorrectionTests(SupplierPaidBillFixture paidBill)
    : IClassFixture<SupplierPaidBillFixture>
{
    [Fact]
    public async Task PaidBill300CorrectedTo280PreservesPaymentAndAdvance20()
    {
        // GIVEN a real reviewed invoice and its embedded three-hundred-dollar payment.
        var prepared = await paidBill.OpenAsync(embedPayment: true);
        await using var context = prepared.Context;
        var bill = prepared.Bill;
        var cash = await context.PaymentCashEvidenceAsync();
        var source = await context.BillSourceEvidenceAsync(bill);
        var replacement = await context.BillReplacementAsync(bill, "280");
        var successor = Guid.Parse(replacement["post"]!["billId"]!.ToString());
        var funding = prepared.Advance;
        var command = await context.CorrectionCommandAsync("Bill", bill, replacement);
        command["reapplications"]!.AsArray().Add(new JsonObject
        {
            ["fundingItemId"] = funding.ToString(),
            ["debtItemId"] = successor.ToString(),
            ["amount"] = "280"
        });
        // WHEN the complete bill owner replaces the obligation and explicitly reapplies the payment.
        var preview = await context.PreviewCorrectionAsync("Bill", command);
        Assert.Equal(20m, SupplierCorrectionPreviewContractTests.RemainingBalance(preview, funding));
        Assert.Equal(0m, SupplierCorrectionPreviewContractTests.RemainingBalance(preview, successor));
        var journals = Assert.IsType<JsonArray>(preview["proposedJournals"]);
        Assert.Equal(new[] { "Application", "Replacement", "ReverseApplication", "ReverseSource" },
            journals.Select(journal => journal!["memberKind"]!.ToString()).Order());
        command["expectedPlanFingerprint"] = preview["planFingerprint"]!.DeepClone();
        var correction = await context.CorrectAsync("Bill", command);
        await SupplierCorrectionPreviewContractTests.AssertJournalLinesAsync(context, preview, correction);
        // THEN the bank payment stays exact and only twenty remains available as an advance.
        Assert.Equal((0m, 20m, -300m), (await context.BalanceAsync(successor),
            await context.BalanceAsync(funding), await context.BankBalanceAsync()));
        Assert.Equal(0m, await context.BalanceAsync(bill));
        Assert.Equal(280m, await context.Bills.Recognition.BalanceAsync("Prepayment"));
        Assert.Equal(cash, await context.PaymentCashEvidenceAsync());
        Assert.Equal(source, await context.BillSourceEvidenceAsync(bill));
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
    }

    [Theory]
    [InlineData("280", "100", "180")]
    [InlineData("340", "300", "40")]
    public async Task LaterStandaloneApplicationReleasesOnce(string replacementAmount, string applied, string remaining)
    {
        // GIVEN an invoice paid later through a standalone advance application.
        var prepared = await paidBill.OpenAsync(embedPayment: false);
        await using var context = prepared.Context;
        var bill = prepared.Bill;
        var funding = prepared.Advance;
        await context.Payments.Allocation.ApplyAsync(await context.Payments.Allocation.CommandAsync(funding, bill, applied));
        var cash = await context.PaymentCashEvidenceAsync();
        var replacement = await context.BillReplacementAsync(bill, replacementAmount);
        var successor = Guid.Parse(replacement["post"]!["billId"]!.ToString());
        var command = await context.CorrectionCommandAsync("Bill", bill, replacement);
        command["reapplications"]!.AsArray().Add(new JsonObject
        {
            ["fundingItemId"] = funding.ToString(),
            ["debtItemId"] = successor.ToString(),
            ["amount"] = applied
        });
        // WHEN the source correction replaces the bill and reapplies only the explicitly chosen amount.
        await context.CaptureBillCorrectionPlanAsync(command); await context.CorrectAsync("Bill", command);
        // THEN payable and advance positions reflect one release and the unchanged actual payment.
        Assert.Equal(decimal.Parse(remaining, System.Globalization.CultureInfo.InvariantCulture), await context.BalanceAsync(successor));
        Assert.Equal(300m - decimal.Parse(applied, System.Globalization.CultureInfo.InvariantCulture), await context.BalanceAsync(funding));
        Assert.Equal(0m, await context.BalanceAsync(bill));
        Assert.Equal(-300m, await context.BankBalanceAsync());
        Assert.Equal(cash, await context.PaymentCashEvidenceAsync());
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
    }

    [Fact]
    public async Task BillCorrectionCannotReapplyFundsPostedAfterItsSelectedDate()
    {
        // GIVEN a reviewed successor bill and funding that posts after the correction date.
        var prepared = await paidBill.OpenAsync(embedPayment: true);
        await using var context = prepared.Context;
        var latePayment = await context.Payments.CommandAsync("100", "2026-09-25");
        await context.Payments.RecordAsync(latePayment);
        var replacement = await context.BillReplacementAsync(prepared.Bill, "280");
        var command = await context.CorrectionCommandAsync("Bill", prepared.Bill, replacement);
        command["reapplications"]!.AsArray().Add(new JsonObject
        {
            ["fundingItemId"] = latePayment["paymentId"]!.DeepClone(),
            ["debtItemId"] = replacement["post"]!["billId"]!.DeepClone(),
            ["amount"] = "100"
        });
        // WHEN previewing THEN it rejects the whole group instead of moving the application to September 25.
        var error = await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Bill", command));
        Assert.Equal(51004, error.Number);
    }
}
