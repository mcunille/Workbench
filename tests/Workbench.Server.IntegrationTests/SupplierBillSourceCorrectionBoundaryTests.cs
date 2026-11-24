// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierBillSourceCorrectionBoundaryTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task WithdrawingBillAndReplacingItsCreditDoesNotRequireUnusedBillReviewOrPostingAuthority()
    {
        // GIVEN two authentic bills and a credit on the matched bill being withdrawn.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer); await context.EnableBillCorrectionsAsync();
        var bill = await context.ExpenseBillAsync("100"); var otherBill = await context.InvoiceOnlyBillAsync("100");
        var credit = Guid.Parse((await context.CreditAsync(bill, "18"))["sourceId"]!.ToString());
        var replacementCredit = await context.CreditCommandAsync(otherBill, "10", "2026-09-20");
        var command = await context.CorrectionCommandAsync("Bill", bill, null);
        command["sourceDecisions"]!.AsArray().Add(await context.MatchedReceiptDecisionAsync(bill, null));
        command["sourceDecisions"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
        {
            ["sourceKind"] = "SupplierCredit",
            ["sourceId"] = credit.ToString(),
            ["expectedSourceVersion"] = (await context.CorrectionCommandAsync("Credit", credit, null))["expectedSourceVersion"]!.DeepClone(),
            ["replacement"] = replacementCredit
        });
        await context.Bills.AdminAsync("DELETE FROM [Identity].RoleClaims WHERE ClaimType='workbench/permission' AND ClaimValue IN('SupplierBillsManage','SupplierBillsPost')");
        // WHEN current bill-correction and credit-correction/posting authority remain, with no successor bill requested.
        await context.CaptureBillCorrectionPlanAsync(command); var request = Guid.NewGuid();
        await context.CorrectAsync("Bill", command, request); await context.CorrectAsync("Bill", command, request);
        // THEN only the actually requested source families post, and the existing other bill retains its gross payable.
        Assert.Equal(0m, await context.BalanceAsync(bill)); Assert.Equal(0m, await context.BalanceAsync(credit));
        Assert.Equal(100m, await context.BalanceAsync(otherBill));
        Assert.Equal(10m, await context.BalanceAsync(Guid.Parse(replacementCredit["creditId"]!.ToString())));
        Assert.Equal(90m, await context.Bills.Recognition.BalanceAsync("Prepayment"));
        Assert.Equal(0m, await context.Bills.Recognition.BalanceAsync("Expense")); Assert.Equal(0m, await context.BankBalanceAsync());
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
    }

    [Fact]
    public async Task ClosedOriginalBillUsesAnOpenCorrectionDateAndPreservesEffectiveDates()
    {
        // GIVEN an authentic invoice whose original posting month has been reconciled and closed.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        await context.EnableBillCorrectionsAsync();
        var bill = await context.InvoiceOnlyBillAsync("100"); var source = await context.BillSourceEvidenceAsync(bill);
        await context.Bills.AdminAsync(JournalControlAdapterSql.Install);
        await using (var close = new SqlCommand("EXEC Accounting.CloseSyntheticPeriod @actor,@session,@request,@version,'2026-09-01',N'Reconciled',NULL", context.Bills.Journal.Connection))
        {
            close.Parameters.AddWithValue("@actor", JournalTestContext.ActorId); close.Parameters.AddWithValue("@session", context.Bills.Journal.SessionId);
            close.Parameters.AddWithValue("@request", Guid.NewGuid()); close.Parameters.AddWithValue("@version", context.Bills.Journal.ConfigurationVersion);
            await close.ExecuteNonQueryAsync();
        }
        var replacement = await context.BillReplacementAsync(bill, "90", "2026-10-01");
        var command = await context.CorrectionCommandAsync("Bill", bill, replacement, "2026-10-01");
        // WHEN the owner corrects on an open posting date with the actual original effective dates.
        await context.CaptureBillCorrectionPlanAsync(command); var result = await context.CorrectAsync("Bill", command);
        var group = Guid.Parse(result["groupId"]!.ToString()); var successor = Guid.Parse(replacement["post"]!["billId"]!.ToString());
        // THEN the closed-period source survives and only open-date monetary corrections are added.
        Assert.Equal(source, await context.BillSourceEvidenceAsync(bill)); Assert.Equal(0m, await context.BalanceAsync(bill));
        Assert.Equal(90m, await context.BalanceAsync(successor));
        Assert.Equal(0, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.SupplierItemMovements WHERE GroupId='{group}' AND PostingDate<>'2026-10-01'"));
        Assert.Equal("2026-02-01", await context.Bills.ScalarAsync<string>($"SELECT CONVERT(varchar(10),actual.EffectiveDate,23) FROM Purchasing.SupplierBillPostingEvents posting JOIN Purchasing.RecognitionSideEvents actual ON actual.TenantId=posting.TenantId AND actual.Id=posting.EventId WHERE posting.BillId='{successor}'"));
    }

    [Fact]
    public async Task ExplicitBillWithdrawalRestoresThePaidAdvanceWithoutTouchingCash()
    {
        // GIVEN a real paid invoice whose supplier obligation is explicitly withdrawn.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        await context.EnableBillCorrectionsAsync();
        var bill = await context.InvoiceOnlyBillAsync("300");
        var payment = await context.Payments.CommandAsync("300"); await context.Payments.AllocateAsync(payment, bill, "300");
        await context.Payments.RecordAsync(payment);
        var cash = await context.PaymentCashEvidenceAsync(); var source = await context.BillSourceEvidenceAsync(bill);
        var command = await context.CorrectionCommandAsync("Bill", bill, null);
        // WHEN the complete owner records the explicit null replacement decision.
        await context.CaptureBillCorrectionPlanAsync(command); await context.CorrectAsync("Bill", command);
        // THEN the actual payment becomes a full advance and the posted original is still retained.
        Assert.Equal(0m, await context.BalanceAsync(bill));
        Assert.Equal(300m, await context.BalanceAsync(Guid.Parse(payment["paymentId"]!.ToString())));
        Assert.Equal(0m, await context.Bills.Recognition.BalanceAsync("Prepayment"));
        Assert.Equal(-300m, await context.BankBalanceAsync());
        Assert.Equal(cash, await context.PaymentCashEvidenceAsync()); Assert.Equal(source, await context.BillSourceEvidenceAsync(bill));
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
    }

    [Fact]
    public async Task ASecondGenerationUsesFreshBillAndRecognitionIdentities()
    {
        // GIVEN an invoice already corrected into a genuine reviewed successor.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        await context.EnableBillCorrectionsAsync();
        var original = await context.InvoiceOnlyBillAsync("100");
        var first = await context.BillReplacementAsync(original, "90");
        var firstId = Guid.Parse(first["post"]!["billId"]!.ToString());
        var command = await context.CorrectionCommandAsync("Bill", original, first);
        await context.CaptureBillCorrectionPlanAsync(command); await context.CorrectAsync("Bill", command);
        var source = await context.BillSourceEvidenceAsync(original); var firstSource = await context.BillSourceEvidenceAsync(firstId);
        var second = await context.BillReplacementAsync(firstId, "80", "2026-09-22");
        var secondId = Guid.Parse(second["post"]!["billId"]!.ToString());
        command = await context.CorrectionCommandAsync("Bill", firstId, second, "2026-09-22");
        // WHEN the surviving successor is corrected through its own complete source owner.
        await context.CaptureBillCorrectionPlanAsync(command); await context.CorrectAsync("Bill", command);
        // THEN both old bills close and the independently allocatable fresh bill carries eighty.
        Assert.Equal(0m, await context.BalanceAsync(original)); Assert.Equal(0m, await context.BalanceAsync(firstId));
        Assert.Equal(80m, await context.BalanceAsync(secondId));
        Assert.Equal(source, await context.BillSourceEvidenceAsync(original)); Assert.Equal(firstSource, await context.BillSourceEvidenceAsync(firstId));
        Assert.Equal(80m, await context.Bills.Recognition.BalanceAsync("Prepayment"));
        Assert.Equal(2, await context.Bills.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierSourceCorrections WHERE SourceKind='SupplierBill'"));
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
    }

    [Fact]
    public async Task ExactReplayRequiresCurrentUpstreamCorrectionAuthority()
    {
        // GIVEN a fully corrected matched service and the completed owner request.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        await context.EnableBillCorrectionsAsync();
        var bill = await context.ExpenseBillAsync("100"); var replacement = await context.BillReplacementAsync(bill, "90");
        var command = await context.CorrectionCommandAsync("Bill", bill, replacement);
        command["sourceDecisions"]!.AsArray().Add(await context.MatchedReceiptDecisionAsync(bill, replacement));
        await context.CaptureBillCorrectionPlanAsync(command);
        var request = Guid.NewGuid(); await context.CorrectAsync("Bill", command, request);
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);
        // WHEN the actor loses current producer correction authority THEN identical replay is denied.
        await context.Bills.AdminAsync("DELETE FROM [Identity].RoleClaims WHERE ClaimType='workbench/permission' AND ClaimValue='PurchaseRecognitionFixtureCorrect'");
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync("Bill", command, request))).Number);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
    }
}
