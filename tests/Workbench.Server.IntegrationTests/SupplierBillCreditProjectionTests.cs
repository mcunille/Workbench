// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierBillCreditProjectionTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task NewRecognitionOfAnExistingReplacementClaimTargetRequiresARefreshedPlan()
    {
        // GIVEN a complete bill correction whose new credit explicitly targets another real unrecognized bill.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var proposal = await context.PrepareBillAndCreditSuccessorsAsync(); var otherBill = await context.InvoiceOnlyBillAsync("100");
        var target = await context.CreditCommandAsync(otherBill, "10", "2026-09-20");
        proposal.Command["sourceDecisions"]![1]!["replacement"]!["claims"] = target["claims"]!.DeepClone();
        await context.CaptureBillCorrectionPlanAsync(proposal.Command);
        var unit = await context.Bills.ScalarAsync<Guid>($"SELECT actual.UnitId FROM Purchasing.SupplierBillPostingEvents posting JOIN Purchasing.RecognitionSideEvents actual ON actual.TenantId=posting.TenantId AND actual.Id=posting.EventId WHERE posting.BillId='{otherBill}'");
        await context.RecognizeAsync(unit, "100", "2026-09-19");
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);
        // WHEN an actual backdated recognition changes the target treatment from prepayment to expense.
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync("Bill", proposal.Command))).Number);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
        // THEN refreshed current evidence can commit the explicitly changed cost treatment.
        await context.CaptureBillCorrectionPlanAsync(proposal.Command); await context.CorrectAsync("Bill", proposal.Command);
        Assert.Equal(180m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(0m, await context.Bills.Recognition.BalanceAsync("Prepayment"));
        Assert.Equal(8m, await context.BalanceAsync(proposal.Clearing));
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
    }

    [Theory]
    [InlineData("Expense")]
    [InlineData("Inventory")]
    public async Task UnrecognizedSuccessorCreditUsesTheActualPrepaymentPosition(string classification)
    {
        // GIVEN an actual invoice-first cost and a credit whose original treatment is prepayment.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer); await context.EnableBillCorrectionsAsync();
        var bill = await context.InvoiceOnlyBillAsync("100", classification);
        var original = await context.CreditAsync(bill, "18"); var credit = Guid.Parse(original["sourceId"]!.ToString());
        var replacement = await context.BillReplacementAsync(bill, "90"); var successor = Guid.Parse(replacement["post"]!["billId"]!.ToString());
        var newCredit = await context.CreditCommandAsync(bill, "10", "2026-09-20"); var newCreditId = Guid.Parse(newCredit["creditId"]!.ToString());
        newCredit["claims"]![0]!["billId"] = successor.ToString();
        newCredit["claims"]![0]!["billRevisionId"] = replacement["post"]!["revisionId"]!.DeepClone();
        newCredit["claims"]![0]!["recognitionEventId"] = replacement["unitBindings"]![0]!["invoiceEventId"]!.DeepClone();
        var command = await context.CorrectionCommandAsync("Bill", bill, replacement);
        command["sourceDecisions"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
        {
            ["sourceKind"] = "SupplierCredit",
            ["sourceId"] = credit.ToString(),
            ["expectedSourceVersion"] = (await context.CorrectionCommandAsync("Credit", credit, null))["expectedSourceVersion"]!.DeepClone(),
            ["replacement"] = newCredit
        });
        // WHEN a genuine reviewed successor and its credit replace the still-unrecognized original cost.
        await context.CaptureBillCorrectionPlanAsync(command); await context.CorrectAsync("Bill", command);
        // THEN only net prepayment changes; no recognized inventory or expense position is invented.
        Assert.Equal(80m, await context.Bills.Recognition.BalanceAsync("Prepayment"));
        Assert.Equal(0m, await context.Bills.Recognition.BalanceAsync("Inventory")); Assert.Equal(0m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(0m, await context.BalanceAsync(bill)); Assert.Equal(0m, await context.BalanceAsync(credit));
        Assert.Equal(90m, await context.BalanceAsync(successor)); Assert.Equal(10m, await context.BalanceAsync(newCreditId));
        Assert.Equal(0m, await context.BankBalanceAsync());
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
    }

    [Fact]
    public async Task FutureClaimsCannotInventBillProvenanceTaxCapacityOrExistingItemIdentity()
    {
        // GIVEN complete authentic bill, receipt, credit and refund owners plus reviewed successors.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var proposal = await context.PrepareBillAndCreditSuccessorsAsync();
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments); var cash = await context.CashEvidenceAsync();
        // WHEN future claims borrow a revision, tax component, excess capacity or an existing item identity.
        foreach (var invalid in new[] { "revision", "tax", "capacity", "item" })
        {
            var command = proposal.Command.DeepClone().AsObject(); var credit = command["sourceDecisions"]![1]!["replacement"]!;
            switch (invalid)
            {
                case "revision": credit["claims"]![0]!["billRevisionId"] = Guid.NewGuid().ToString(); break;
                case "tax": credit["claims"]![0]!["componentKey"] = "tax"; break;
                case "capacity": credit["total"] = "91"; credit["claims"]![0]!["amount"] = "91"; break;
                case "item": credit["creditId"] = proposal.Bill.ToString(); break;
            }
            Assert.Equal(invalid is "capacity" or "item" ? 51009 : 51004,
                (await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Bill", command))).Number);
            Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
        }
        // THEN valid reviewed provenance still posts through the actual owner with unchanged cash.
        await context.CaptureBillCorrectionPlanAsync(proposal.Command); await context.CorrectAsync("Bill", proposal.Command);
        Assert.Equal(80m, await context.Bills.Recognition.BalanceAsync("Expense")); Assert.Equal(8m, await context.BalanceAsync(proposal.Clearing));
        Assert.Equal(cash, await context.CashEvidenceAsync());
    }

    [Fact]
    public async Task CompleteBillCompositionRechecksEveryCurrentFamilyBeforePreviewAndReplay()
    {
        // GIVEN a full cross-family proposal whose actual refund is preserved and whose successors are reviewed.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        var proposal = await context.PrepareBillAndCreditSuccessorsAsync();
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);
        var permissions = new[] { "SupplierBillsManage", "SupplierBillsCorrect", "SupplierBillsPost", "SupplierCreditsCorrect",
            "SupplierCreditsPost", "SupplierAllocationsManage", "PurchaseRecognitionFixtureCorrect", "PurchaseRecognitionFixturePost" };
        foreach (var permission in permissions)
        {
            await RemoveAsync(permission);
            try { Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Bill", proposal.Command))).Number); }
            finally { await RestoreAsync(permission); }
            Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
        }
        await context.CaptureBillCorrectionPlanAsync(proposal.Command); var request = Guid.NewGuid();
        await context.CorrectAsync("Bill", proposal.Command, request);
        var posted = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);
        // WHEN each current permission is independently removed THEN exact replay cannot rely on earlier authority.
        foreach (var permission in permissions)
        {
            await RemoveAsync(permission);
            try { Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync("Bill", proposal.Command, request))).Number); }
            finally { await RestoreAsync(permission); }
            Assert.Equal(posted, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
        }
        Assert.Equal(80m, await context.Bills.Recognition.BalanceAsync("Expense"));

        Task RemoveAsync(string permission) => context.Bills.AdminAsync($"DELETE FROM [Identity].RoleClaims WHERE ClaimType='workbench/permission' AND ClaimValue='{permission}'");
        Task RestoreAsync(string permission) => context.Bills.AdminAsync($"INSERT [Identity].RoleClaims(TenantId,RoleId,ClaimType,ClaimValue) SELECT TenantId,RoleId,N'workbench/permission',N'{permission}' FROM Administration.AccountingRoles WHERE Kind='Administrator'");
    }
}
