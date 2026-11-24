// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierCreditCorrectionJournalTests(SupplierCreditJournalFixture fixture) : IClassFixture<SupplierCreditJournalFixture>
{
    [Fact]
    public async Task RefundedCredit18CorrectedTo10PreservesCashAndLeavesDebt()
    {
        // GIVEN a paid bill, authentic credit and actual refund of eighteen.
        var prepared = await fixture.OpenAsync(refund: true);
        await using var context = prepared.Context;
        var (bill, original, settlement) = (prepared.Bill, prepared.Credit, prepared.Settlement!.Value);
        var cashBefore = await context.CashEvidenceAsync(); var originalBefore = await context.CreditEvidenceAsync(original);
        var replacement = await context.CreditCommandAsync(bill, "10", "2026-09-20");
        var successor = Guid.Parse(replacement["creditId"]!.GetValue<string>());
        var command = await context.CorrectionCommandAsync("Credit", original, replacement);
        command["reapplications"]!.AsArray().Add(new JsonObject
        {
            ["fundingItemId"] = successor.ToString(),
            ["debtItemId"] = settlement.ToString(),
            ["amount"] = "10"
        });
        // WHEN the complete source correction explicitly reapplies ten to the released receipt debt.
        var preview = await context.PreviewCorrectionAsync("Credit", command);
        var journals = Assert.IsType<JsonArray>(preview["proposedJournals"]);
        Assert.Equal(new[] { "Application", "ReleaseSettlement", "Replacement", "ReverseSource" },
            journals.Select(journal => journal!["memberKind"]!.ToString()).Order());
        command["expectedPlanFingerprint"] = preview["planFingerprint"]!.DeepClone();
        var result = await context.CorrectAsync("Credit", command);
        await SupplierCorrectionPreviewContractTests.AssertJournalLinesAsync(context, preview, result);
        var group = Guid.Parse(result["groupId"]!.GetValue<string>());
        // THEN actual cash and original evidence remain exact while eight is owed to the supplier.
        Assert.Equal(cashBefore, await context.CashEvidenceAsync());
        Assert.Equal(originalBefore, await context.CreditEvidenceAsync(original));
        Assert.Equal(0m, await context.BalanceAsync(original));
        Assert.Equal(0m, await context.BalanceAsync(successor));
        Assert.Equal(8m, await context.BalanceAsync(settlement));
        Assert.Equal(-82m, await context.BankBalanceAsync());
        Assert.Equal(90m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(group, await context.Bills.ScalarAsync<Guid>($"SELECT GroupId FROM Purchasing.SupplierCredits WHERE Id='{successor}'"));
        Assert.Equal(group, await context.Bills.ScalarAsync<Guid>($"SELECT GroupId FROM Purchasing.SupplierCashSettlementReleases WHERE SettlementId='{settlement}'"));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>($"SELECT COUNT(DISTINCT RecordedAtUtc) FROM Purchasing.SupplierItemMovements WHERE GroupId='{group}'"));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Accounting.JournalLines line JOIN Accounting.JournalEntries journal ON journal.TenantId=line.TenantId AND journal.Id=line.JournalId WHERE line.AccountPurpose IN('Bank','Cash') AND journal.Id IN(SELECT JournalId FROM Purchasing.SupplierSourceCorrectionMembers WHERE GroupId='{group}' AND JournalId IS NOT NULL)"));
    }

    [Fact]
    public async Task AllocatedCreditCorrectionReleasesAndExplicitlyReapplies()
    {
        // GIVEN a credit of thirty with twenty applied to its bill.
        var prepared = await fixture.OpenAsync(refund: false);
        await using var context = prepared.Context;
        var (bill, original) = (prepared.Bill, prepared.Credit);
        var replacement = await context.CreditCommandAsync(bill, "25", "2026-09-20");
        var successor = Guid.Parse(replacement["creditId"]!.GetValue<string>());
        replacement["allocations"]!.AsArray().Add(new JsonObject
        {
            ["billId"] = bill.ToString(),
            ["itemId"] = bill.ToString(),
            ["expectedItemVersion"] = await context.Payments.Allocation.VersionAsync(bill),
            ["amount"] = "7.50"
        });
        var command = await context.CorrectionCommandAsync("Credit", original, replacement);
        command["reapplications"]!.AsArray().Add(new JsonObject { ["fundingItemId"] = successor.ToString(), ["debtItemId"] = bill.ToString(), ["amount"] = "7.50" });
        // WHEN two equivalent allocations are requested through the two existing intent fields.
        var preview = await context.PreviewCorrectionAsync("Credit", command);
        var journals = Assert.IsType<JsonArray>(preview["proposedJournals"]);
        Assert.Contains(journals, journal => journal!["memberKind"]!.ToString() == "ReverseApplication");
        Assert.Equal(new[] { 0, 1 }, journals.Where(journal => journal!["memberKind"]!.ToString() == "Application")
            .Select(journal => journal!["position"]!.GetValue<int>()).Order());
        command["expectedPlanFingerprint"] = preview["planFingerprint"]!.DeepClone();
        var result = await context.CorrectAsync("Credit", command);
        await SupplierCorrectionPreviewContractTests.AssertJournalLinesAsync(context, preview, result);
        // THEN gross capacity is replaced once and only fifteen remains applied.
        Assert.Equal(0m, await context.BalanceAsync(original));
        Assert.Equal(10m, await context.BalanceAsync(successor));
        Assert.Equal(85m, await context.BalanceAsync(bill));
        Assert.Equal(75m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(0m, await context.BankBalanceAsync());
    }
}
