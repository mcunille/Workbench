// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
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
        Assert.Equal(2, journals.Count(journal => journal!["memberKind"]!.ToString() == "ReverseApplication"));
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

    [Fact]
    public async Task EqualInverseLinesCannotSwapOriginalApplicationOwnership()
    {
        // GIVEN two posted applications with identical journal lines and distinct original ownership.
        var prepared = await fixture.OpenAsync(refund: false);
        await using var context = prepared.Context;
        var input = await context.CorrectionCommandAsync("Credit", prepared.Credit, null);
        await context.CaptureCorrectionPlanAsync(input);
        await context.Bills.AdminAsync("""
            DECLARE @definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.CorrectSupplierCredit')),
              @details nvarchar(200)=N'SET @Details=(SELECT @Inverse reversalId,@Entity applicationId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);',
              @member nvarchar(200)=N'VALUES(@Tenant,NEWID(),@Correction,@Group,''ReverseApplication'',@Entity,@Journal,@Details);';
            IF CHARINDEX(@details,@definition)=0 OR CHARINDEX(@member,@definition)=0
              THROW 50000,'Expected reversal membership boundary missing.',1;
            SET @definition=REPLACE(@definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @definition=REPLACE(@definition,@details,@details+N'
              DECLARE @WrongApplication uniqueidentifier=(SELECT Id FROM OPENJSON(@Plan,''$.applications'')
                WITH(Id uniqueidentifier ''$.applicationId'') WHERE Id<>@Entity);');
            SET @definition=REPLACE(@definition,@member,REPLACE(@member,N'@Entity',N'@WrongApplication'));
            EXEC sys.sp_executesql @definition;
            """);
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);

        // WHEN an installed writer swaps the member links THEN the parity check rejects the entire correction.
        var error = await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync("Credit", input));
        Assert.Equal(51004, error.Number);
        Assert.Contains("approved journal", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
    }

    [Fact]
    public async Task ReplacementGroupsSharedExpenseControlsByTheirHistoricalRevision()
    {
        // GIVEN two posted bills using different revisions of the same expense account.
        var prepared = await fixture.OpenAsync(refund: false);
        await using var context = prepared.Context;
        var expense = context.Bills.Recognition.Accounts["Expense"];
        var oldVersion = await context.Bills.ScalarAsync<Guid>($"SELECT Version FROM Accounting.Accounts WHERE Id='{expense}'");
        var code = await context.Bills.ScalarAsync<string>($"SELECT Code FROM Accounting.Accounts WHERE Id='{expense}'");
        var newRevision = await context.Bills.Journal.SaveAsync(Guid.NewGuid(), "UpdateAccount",
            new JsonObject { ["code"] = code, ["name"] = "Expense after first bill" }.ToJsonString(), expense, oldVersion);
        var secondBill = await context.ExpenseBillAsync("100");
        var replacement = await context.CreditCommandAsync(prepared.Bill, "21", "2026-09-20");
        var claims = replacement["claims"]!.AsArray();
        claims[0]!["amount"] = "10";
        var freight = claims[0]!.DeepClone();
        freight["claimId"] = Guid.NewGuid().ToString();
        freight["componentKey"] = "freight";
        freight["amount"] = "4";
        claims.Add(freight);
        var secondClaim = (await context.CreditCommandAsync(secondBill, "7", "2026-09-20"))["claims"]![0]!;
        claims.Add(secondClaim.DeepClone());
        var input = await context.CorrectionCommandAsync("Credit", prepared.Credit, replacement);

        // WHEN the replacement claims both revisions THEN the old base and freight combine on one line only.
        var preview = await context.PreviewCorrectionAsync("Credit", input);
        var journal = Assert.Single(preview["proposedJournals"]!.AsArray(), member => member!["memberKind"]!.ToString() == "Replacement");
        var lines = journal!["lines"]!.AsArray();
        Assert.Equal(new[] { 1, 2, 3 }, lines.Select(line => line!["ordinal"]!.GetValue<int>()));
        Assert.Equal("21.0000", lines[0]!["debit"]!.ToString());
        Assert.Equal("0.0000", lines[0]!["credit"]!.ToString());
        var costs = lines.Skip(1).ToDictionary(line => Guid.Parse(line!["accountVersion"]!.ToString()));
        Assert.Equal("14.0000", costs[oldVersion]!["credit"]!.ToString());
        Assert.Equal("7.0000", costs[newRevision.Version]!["credit"]!.ToString());
        Assert.All(costs.Values, line =>
        {
            Assert.Equal(expense, Guid.Parse(line!["accountId"]!.ToString()));
            Assert.Equal("0.0000", line["debit"]!.ToString());
        });
        input["expectedPlanFingerprint"] = preview["planFingerprint"]!.DeepClone();
        var result = await context.CorrectAsync("Credit", input);
        await SupplierCorrectionPreviewContractTests.AssertJournalLinesAsync(context, preview, result);
        Assert.Equal(179m, await context.Bills.Recognition.BalanceAsync("Expense"));
    }

    [Fact]
    public async Task ExactReplacementLinesCannotNameADifferentCredit()
    {
        // GIVEN a real replacement and a disposable writer that records the wrong correction member identity.
        var prepared = await fixture.OpenAsync(refund: false);
        await using var context = prepared.Context;
        var replacement = await context.CreditCommandAsync(prepared.Bill, "25", "2026-09-20");
        var input = await context.CorrectionCommandAsync("Credit", prepared.Credit, replacement);
        await context.CaptureCorrectionPlanAsync(input);
        await context.Bills.AdminAsync("""
            DECLARE @definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.PostSupplierCreditCore')),
              @member nvarchar(200)=N'VALUES(@Tenant,NEWID(),@ParentCorrection,@Group,''Replacement'',@Credit,@Journal,@Credit,@Canonical);';
            IF CHARINDEX(@member,@definition)=0 THROW 50000,'Expected replacement membership boundary missing.',1;
            SET @definition=REPLACE(@definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @definition=REPLACE(@definition,@member,REPLACE(@member,N'''Replacement'',@Credit',N'''Replacement'',NEWID()'));
            EXEC sys.sp_executesql @definition;
            """);
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);

        // WHEN the lines and balances match but the named credit differs THEN the correction rolls back.
        var error = await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync("Credit", input));
        Assert.Equal(51004, error.Number);
        Assert.Contains("approved journal", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
    }
}
