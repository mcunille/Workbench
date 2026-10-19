// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed partial class SupplierCreditCorrectionTests
{
    [Fact]
    public async Task NewCreditUsesEverySurvivingCorrectionBoundary()
    {
        // GIVEN twenty replaced by seventy on a later open date.
        await using var context = await SupplierCreditTestContext.OpenAsync(Fixture);
        var bill = await context.ExpenseBillAsync("100");
        var first = await context.CreditAsync(bill, "20");
        var original = Guid.Parse(first["sourceId"]!.GetValue<string>());
        var replacement = await context.CreditCommandAsync(bill, "70", "2026-09-20");
        var command = await context.CorrectionCommandAsync("Credit", original, replacement);
        await context.CaptureCorrectionPlanAsync(command);
        await context.CorrectAsync("Credit", command);
        // WHEN thirty is posted before that correction date THEN the timeline is fifty followed by one hundred.
        var laterRecorded = await context.CreditAsync(bill, "30", "2026-09-18");
        // THEN the superseded twenty is not counted alongside its seventy-dollar successor after that date.
        Assert.Equal(30m, await context.BalanceAsync(Guid.Parse(laterRecorded["sourceId"]!.GetValue<string>())));
        Assert.Equal(0m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(0m, await context.BalanceAsync(original));
    }

    [Fact]
    public async Task ReplacementComponentDiscoversTheOtherCreditOwningItsCapacity()
    {
        // GIVEN two genuine bill components and a credit already owning all of the second component.
        await using var context = await SupplierCreditTestContext.OpenAsync(Fixture);
        var firstBill = await context.ExpenseBillAsync("100");
        var secondBill = await context.ExpenseBillAsync("100");
        var first = await context.CreditAsync(firstBill, "20");
        var other = await context.CreditAsync(secondBill, "100");
        var original = Guid.Parse(first["sourceId"]!.GetValue<string>());
        var related = Guid.Parse(other["sourceId"]!.GetValue<string>());
        var replacement = await context.CreditCommandAsync(secondBill, "70", "2026-09-20");
        var relatedReplacement = await context.CreditCommandAsync(secondBill, "30", "2026-09-20");
        var command = await context.CorrectionCommandAsync("Credit", original, replacement);
        // WHEN the successor changes attribution THEN the new component's existing owner still constrains it.
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Credit", command))).Number);
        command["sourceDecisions"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
        {
            ["sourceKind"] = "SupplierCredit",
            ["sourceId"] = related.ToString(),
            ["expectedSourceVersion"] = await context.Bills.ScalarAsync<string>($"SELECT CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Purchasing.SupplierCreditVersions WHERE CreditId='{related}'"),
            ["replacement"] = relatedReplacement.DeepClone()
        });
        // WHEN both affected sources are explicitly corrected THEN the shared proposal is valid and atomic.
        await context.CaptureCorrectionPlanAsync(command);
        await context.CorrectAsync("Credit", command);
        Assert.Equal(0m, await context.BalanceAsync(original));
        Assert.Equal(0m, await context.BalanceAsync(related));
        Assert.Equal(100m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(2, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierSourceCorrections"));
    }

    [Theory]
    [InlineData(true, 51004)]
    [InlineData(false, 51009)]
    public async Task PreviewValidatesReplacementEvidenceAndRecordedReferenceResolution(bool unavailableEvidence, int expectedError)
    {
        // GIVEN an authentic original credit and a proposed successor.
        await using var context = await SupplierCreditTestContext.OpenAsync(Fixture);
        var bill = await context.ExpenseBillAsync("100");
        var credit = await context.CreditAsync(bill, "18");
        var original = Guid.Parse(credit["sourceId"]!.GetValue<string>());
        var replacement = await context.CreditCommandAsync(bill, "10", "2026-09-20");
        if (unavailableEvidence)
            replacement["evidence"]!["documents"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
            {
                ["documentId"] = Guid.NewGuid().ToString(),
                ["revisionId"] = Guid.NewGuid().ToString()
            });
        else
            replacement["reference"] = await context.Bills.ScalarAsync<string>($"SELECT Reference FROM Purchasing.SupplierCredits WHERE Id='{original}'");
        var command = await context.CorrectionCommandAsync("Credit", original, replacement);
        // WHEN the evidence is unavailable or duplicate resolution is omitted THEN preview must reject before reserving effects.
        Assert.Equal(expectedError, (await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Credit", command))).Number);
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCredits"));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierSourceCorrections"));
        Assert.Equal(18m, await context.BalanceAsync(original));
    }

    [Fact]
    public async Task RelatedCreditReplacementsShareOneComponentCapacityProposal()
    {
        // GIVEN two genuine credits fully claiming one original component.
        await using var context = await SupplierCreditTestContext.OpenAsync(Fixture);
        var bill = await context.ExpenseBillAsync("100");
        var first = await context.CreditAsync(bill, "60"); var firstId = Guid.Parse(first["sourceId"]!.GetValue<string>());
        var second = await context.CreditAsync(bill, "40"); var secondId = Guid.Parse(second["sourceId"]!.GetValue<string>());
        var firstReplacement = await context.CreditCommandAsync(bill, "70", "2026-09-20");
        var secondReplacement = await context.CreditCommandAsync(bill, "30", "2026-09-20");
        var command = await context.CorrectionCommandAsync("Credit", firstId, firstReplacement);
        // WHEN only the first increase is proposed THEN the unchanged second credit still owns its capacity.
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Credit", command))).Number);
        command["sourceDecisions"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
        {
            ["sourceKind"] = "SupplierCredit",
            ["sourceId"] = secondId.ToString(),
            ["expectedSourceVersion"] = await context.Bills.ScalarAsync<string>($"SELECT CONVERT(varchar(18),CONVERT(binary(8),RowVersion),1) FROM Purchasing.SupplierCreditVersions WHERE CreditId='{secondId}'"),
            ["replacement"] = secondReplacement.DeepClone()
        });
        var dependentReplacement = command["sourceDecisions"]![0]!["replacement"]!;
        var independentReference = dependentReplacement["reference"]!.DeepClone();
        dependentReplacement["reference"] = firstReplacement["reference"]!.DeepClone();
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Credit", command))).Number);
        dependentReplacement["reference"] = independentReference;
        // WHEN both explicit decisions rebalance the same capacity THEN they commit as one complete proposal.
        await context.CaptureCorrectionPlanAsync(command); await context.CorrectAsync("Credit", command);
        Assert.Equal(0m, await context.BalanceAsync(firstId)); Assert.Equal(0m, await context.BalanceAsync(secondId));
        Assert.Equal(70m, await context.BalanceAsync(Guid.Parse(firstReplacement["creditId"]!.GetValue<string>())));
        Assert.Equal(30m, await context.BalanceAsync(Guid.Parse(secondReplacement["creditId"]!.GetValue<string>())));
        Assert.Equal(0m, await context.Bills.Recognition.BalanceAsync("Expense"));
    }

    [Fact]
    public async Task ClosedOriginalPeriodCanBeCorrectedOnAnOpenDate()
    {
        // GIVEN a real credit whose original posting month is subsequently closed.
        await using var context = await SupplierCreditTestContext.OpenAsync(Fixture);
        var bill = await context.ExpenseBillAsync("100");
        var credit = await context.CreditAsync(bill, "18"); var original = Guid.Parse(credit["sourceId"]!.GetValue<string>());
        await context.Bills.AdminAsync(JournalControlAdapterSql.Install);
        await using (var close = new SqlCommand("EXEC Accounting.CloseSyntheticPeriod @ActorId=@actor,@SessionId=@session,@RequestId=@request,@ExpectedConfigurationVersion=@version,@PeriodStart='2026-09-01',@Reason=N'Reconciled',@Evidence=NULL", context.Bills.Journal.Connection))
        {
            close.Parameters.AddWithValue("@actor", JournalTestContext.ActorId); close.Parameters.AddWithValue("@session", context.Bills.Journal.SessionId);
            close.Parameters.AddWithValue("@request", Guid.NewGuid()); close.Parameters.AddWithValue("@version", context.Bills.Journal.ConfigurationVersion);
            await close.ExecuteNonQueryAsync();
        }
        var replacement = await context.CreditCommandAsync(bill, "10", "2026-10-01");
        var command = await context.CorrectionCommandAsync("Credit", original, replacement, "2026-10-01");
        // WHEN all correction effects use an open date THEN the closed original history stays intact.
        await context.CaptureCorrectionPlanAsync(command); await context.CorrectAsync("Credit", command);
        Assert.Equal(0m, await context.BalanceAsync(original));
        Assert.Equal(90m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierSourceCorrections WHERE PostingDate<'2026-10-01'"));
    }

    [Fact]
    public async Task ReplacementCannotBorrowAnotherBillsRecognitionProvenance()
    {
        // GIVEN two authentic bills with independent recognition provenance on the same purchase.
        await using var context = await SupplierCreditTestContext.OpenAsync(Fixture);
        var first = await context.ExpenseBillAsync("100"); var second = await context.ExpenseBillAsync("100");
        var credit = await context.CreditAsync(first, "18"); var original = Guid.Parse(credit["sourceId"]!.GetValue<string>());
        var replacement = await context.CreditCommandAsync(first, "10", "2026-09-20");
        var other = await context.CreditCommandAsync(second, "10", "2026-09-20");
        replacement["claims"]![0]!["recognitionEventId"] = other["claims"]![0]!["recognitionEventId"]!.DeepClone();
        var command = await context.CorrectionCommandAsync("Credit", original, replacement);
        // WHEN a real event is attached to the wrong original bill revision THEN ownership validation rejects.
        Assert.Equal(51004, (await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Credit", command))).Number);
        Assert.Equal(18m, await context.BalanceAsync(original));
        Assert.Equal(1, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierCredits"));
    }

}
