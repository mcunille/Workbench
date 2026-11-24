// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCashCorrectionTests(SqlServerFixture sqlServer, SupplierCashClearingFixture clearing) : IClassFixture<SupplierCashClearingFixture>
{
    [Theory]
    [InlineData(true, 10, 8)]
    [InlineData(false, 0, 18)]
    public async Task RefundRecordingCorrectionIsNotAnActualRepayment(bool replace, int bank, int creditBalance)
    {
        // GIVEN a genuine supplier credit and a recorded eighteen-dollar incoming refund whose amount was wrong.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer); await context.EnableCashCorrectionsAsync();
        var credit = await context.CreditAsync(await context.ExpenseBillAsync("100"), "18");
        var creditId = Guid.Parse(credit["sourceId"]!.ToString());
        var refund = await context.RefundAsync([(creditId, "18")]); var original = Guid.Parse(refund["sourceId"]!.ToString());
        var evidence = await context.CashSourceEvidenceAsync(original); var creditEvidence = await context.CreditEvidenceAsync(creditId);
        var replacement = replace ? await context.RefundCommandAsync([(creditId, "10")], "2026-09-20") : null;
        var command = await context.CorrectionCommandAsync("Refund", original, replacement);
        // WHEN the complete source owner reverses that record and optionally records the actual ten-dollar receipt.
        await context.CaptureCashCorrectionPlanAsync("Refund", command);
        var result = await context.CorrectAsync("Refund", command); var group = Guid.Parse(result["groupId"]!.ToString());
        // THEN cash/control lines invert exactly, predecessor evidence stays immutable and no actual repayment is invented.
        Assert.Equal(replace ? 1 : 0, result["settlementIds"]!.AsArray().Count);
        if (replace)
        {
            var settlement = Guid.Parse(result["settlementIds"]![0]!.ToString());
            Assert.Equal(Guid.Parse(replacement!["cashSourceId"]!.ToString()),
                await context.Bills.ScalarAsync<Guid>($"SELECT CashSourceId FROM Purchasing.SupplierCashSettlements WHERE Id='{settlement}'"));
        }
        Assert.Equal((decimal)bank, await context.BankBalanceAsync()); Assert.Equal((decimal)creditBalance, await context.BalanceAsync(creditId));
        Assert.Equal(82m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(evidence, await context.CashSourceEvidenceAsync(original)); Assert.Equal(creditEvidence, await context.CreditEvidenceAsync(creditId));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCashSources WHERE Kind='SupplierRefundRepayment'"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"""
            SELECT COUNT(*) FROM Accounting.CorrectionGroups correction
            JOIN Accounting.JournalEntries journal ON journal.TenantId=correction.TenantId AND journal.Id=correction.OriginalJournalId
            JOIN Accounting.SourceEvents source ON source.TenantId=journal.TenantId AND source.Id=journal.SourceEventId
            JOIN Purchasing.SupplierSourceCorrectionMembers member ON member.TenantId=correction.TenantId AND member.JournalId=correction.ReversalJournalId
            WHERE source.SourceKind='SupplierRefund' AND source.SourceId='{original}' AND member.GroupId='{group}'
            """));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>($"""
            SELECT COUNT(*) FROM Purchasing.SupplierSourceCorrectionMembers member
            JOIN Accounting.CorrectionGroups correction ON correction.TenantId=member.TenantId AND correction.ReversalJournalId=member.JournalId
            JOIN Accounting.JournalLines original ON original.TenantId=correction.TenantId AND original.JournalId=correction.OriginalJournalId
            LEFT JOIN Accounting.JournalLines inverse ON inverse.TenantId=original.TenantId AND inverse.JournalId=correction.ReversalJournalId
              AND inverse.Ordinal=original.Ordinal AND inverse.AccountId=original.AccountId AND inverse.AccountVersion=original.AccountVersion
              AND inverse.Debit=original.Credit AND inverse.Credit=original.Debit
            WHERE member.GroupId='{group}' AND inverse.JournalId IS NULL
            """));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.SupplierSourceCorrections WHERE SourceKind='SupplierRefund' AND SourceId='{original}' AND GroupId='{group}'"));
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
    }

    [Fact]
    public async Task RepaymentRecordingCorrectionRestoresTheRealClearingDebt()
    {
        // GIVEN a real clearing liability of eight and an incorrectly recorded repayment of all eight.
        var prepared = await clearing.OpenAsync(); await using var context = prepared.Context; var history = prepared.History;
        var repayment = await context.RepaymentAsync([(history.Clearing, "8")]); var original = Guid.Parse(repayment["sourceId"]!.ToString());
        var evidence = await context.CashSourceEvidenceAsync(original);
        var replacement = await context.RepaymentCommandAsync([(history.Clearing, "3")], "2026-09-22");
        var command = await context.CorrectionCommandAsync("RefundRepayment", original, replacement, "2026-09-22");
        // WHEN the historical outgoing record is replaced by the actual three-dollar repayment.
        var preview = await context.PreviewCorrectionAsync("RefundRepayment", command);
        Assert.IsType<JsonArray>(preview["proposedJournals"]);
        command["expectedPlanFingerprint"] = preview["planFingerprint"]!.DeepClone();
        var result = await context.CorrectAsync("RefundRepayment", command);
        await SupplierCorrectionPreviewContractTests.AssertJournalLinesAsync(context, preview, result);
        // THEN five remains owed; the original receipt and purchase cost are untouched.
        Assert.Equal(5m, await context.BalanceAsync(history.Clearing)); Assert.Equal(15m, await context.BankBalanceAsync());
        Assert.Equal(90m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(evidence, await context.CashSourceEvidenceAsync(original));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierPayments"));
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
    }
}
