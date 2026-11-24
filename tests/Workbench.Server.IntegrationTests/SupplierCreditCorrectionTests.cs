// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed partial class SupplierCreditCorrectionTests(SqlServerFixture sqlServer)
{
    private SqlServerFixture Fixture => sqlServer;
    [Fact]
    public async Task MissingReplacementCannotSilentlyMeanReversal()
    {
        // GIVEN an authentic unconsumed credit and an incomplete correction intent.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var credit = await context.CreditAsync(await context.ExpenseBillAsync("100"), "18");
        var original = Guid.Parse(credit["sourceId"]!.GetValue<string>());
        var command = await context.CorrectionCommandAsync("Credit", original, null); command.Remove("replacement");
        // WHEN the replacement decision is omitted THEN preview rejects instead of guessing a reversal.
        Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Credit", command))).Number);
        command["replacement"] = null;
        // WHEN reversal is explicit THEN the original source closes without cash or a successor.
        await context.CaptureCorrectionPlanAsync(command); await context.CorrectAsync("Credit", command);
        Assert.Equal(0m, await context.BalanceAsync(original));
        Assert.Equal(100m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(0m, await context.BankBalanceAsync());
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCredits"));
    }

    [Theory]
    [InlineData("2026-09-21", "20", "80", "2026-09-21")]
    [InlineData("2026-09-18", "20", "80", "2026-09-16")]
    [InlineData("2026-09-21", "100", "0", "2026-09-21")]
    [InlineData("2026-09-22", "20", "80", "2026-09-21")]
    [InlineData("2026-09-22", "100", "0", "2026-09-21")]
    public async Task PrepaymentCreditCorrectionPreservesMatchedRecognitionAndQuantities(string date, string originalAmount, string recognizedAmount, string recognitionDate)
    {
        // GIVEN a genuine invoice of one hundred recognized net of its prepayment credit, including zero-only recognition.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var bill = await context.PrepaymentBillAsync("100");
        var source = await context.CreditAsync(bill.BillId, originalAmount); var original = Guid.Parse(source["sourceId"]!.GetValue<string>());
        await context.RecognizeAsync(bill.UnitId, recognizedAmount, recognitionDate);
        var sourceBefore = await context.Bills.ScalarAsync<string>($"SELECT (SELECT * FROM Purchasing.RecognitionSideEvents WHERE UnitId='{bill.UnitId}' ORDER BY Id FOR JSON PATH)");
        var replacement = await context.CreditCommandAsync(bill.BillId, "10", date);
        var command = await context.CorrectionCommandAsync("Credit", original, replacement, date);
        // WHEN correction occurs on or after the recorded match THEN its monetary dependency changes as one closure.
        var preview = await context.PreviewCorrectionAsync("Credit", command);
        var journals = Assert.IsType<JsonArray>(preview["proposedJournals"]);
        if (recognitionDate != "2026-09-16")
        {
            var adjustment = Assert.Single(journals, journal => journal!["memberKind"]!.ToString() == "RecognitionAdjustment");
            Assert.Equal(2, adjustment!["lines"]!.AsArray().Count);
        }
        command["expectedPlanFingerprint"] = preview["planFingerprint"]!.DeepClone();
        var result = await context.CorrectAsync("Credit", command);
        await SupplierCorrectionPreviewContractTests.AssertJournalLinesAsync(context, preview, result);
        var group = Guid.Parse(result["groupId"]!.GetValue<string>());
        Assert.Equal(0, await context.Bills.ScalarAsync<int>($"""
            SELECT COUNT(*) FROM Purchasing.SupplierSourceCorrectionMembers member
            JOIN Accounting.JournalEntries journal ON journal.TenantId=member.TenantId AND journal.Id=member.JournalId
            WHERE member.GroupId='{group}' AND journal.PostingDate<>CONVERT(date,'{date}',23)
            """));
        Assert.Equal(90m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(0m, await context.Bills.Recognition.BalanceAsync("Prepayment"));
        Assert.Equal(-100m, await context.Bills.Recognition.BalanceAsync("SupplierPayable"));
        Assert.Equal(1m, await context.Bills.ScalarAsync<decimal>($"SELECT Quantity FROM Purchasing.RecognitionUnits WHERE Id='{bill.UnitId}'"));
        Assert.Equal(sourceBefore, await context.Bills.ScalarAsync<string>($"SELECT (SELECT * FROM Purchasing.RecognitionSideEvents WHERE UnitId='{bill.UnitId}' ORDER BY Id FOR JSON PATH)"));
    }

    [Fact]
    public async Task CorrectionCannotPrecedeRecognitionThatConsumedTheCredit()
    {
        // GIVEN a net recognition posted after the requested correction date.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var bill = await context.PrepaymentBillAsync("100");
        var credit = await context.CreditAsync(bill.BillId, "20");
        await context.RecognizeAsync(bill.UnitId, "80", "2026-09-21");
        var original = Guid.Parse(credit["sourceId"]!.GetValue<string>());
        var replacement = await context.CreditCommandAsync(bill.BillId, "10", "2026-09-18");
        var command = await context.CorrectionCommandAsync("Credit", original, replacement, "2026-09-18");
        // WHEN previewing that date THEN the complete group is rejected.
        Assert.Equal(51004, (await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Credit", command))).Number);
        Assert.Equal(80m, await context.Bills.Recognition.BalanceAsync("Expense"));
    }
}
