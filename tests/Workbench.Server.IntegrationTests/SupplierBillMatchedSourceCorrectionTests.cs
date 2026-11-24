// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierBillMatchedSourceCorrectionTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task ANewPublishedProducerRevisionInvalidatesTheCapturedBillPlan()
    {
        // GIVEN a complete matched correction preview bound to a genuinely published source revision.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer); await context.EnableBillCorrectionsAsync();
        var bill = await context.ExpenseBillAsync("100"); var replacement = await context.BillReplacementAsync(bill, "90");
        var command = await context.CorrectionCommandAsync("Bill", bill, replacement);
        command["sourceDecisions"]!.AsArray().Add(await context.MatchedReceiptDecisionAsync(bill, replacement));
        await context.CaptureBillCorrectionPlanAsync(command);
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);
        var originalEvent = Guid.Parse(command["sourceDecisions"]![0]!["sourceId"]!.ToString());
        var sourceId = await context.Bills.ScalarAsync<Guid>($"SELECT SourceId FROM Purchasing.RecognitionSideEvents WHERE Id='{originalEvent}'");
        var prepared = await context.Bills.Recognition.CommandAsync(classification: "Expense", cost: "100");
        var side = prepared["units"]![0]!["sides"]![0]!; var temporarySource = side["sourceId"]!.ToString();
        side["sourceId"] = sourceId.ToString(); await context.Bills.Recognition.PublishSourceRevisionAsync(side, temporarySource);
        // WHEN the current producer advances its real source head THEN the old plan cannot post any financial participant.
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.CorrectAsync("Bill", command))).Number);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
        command["sourceDecisions"]![0]!["replacement"]!["sourceRevision"] = side["sourceRevision"]!.DeepClone();
        // AND a refreshed preview of the actual new producer revision can still complete the correction.
        await context.CaptureBillCorrectionPlanAsync(command); await context.CorrectAsync("Bill", command);
        Assert.Equal(90m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(0m, await context.BalanceAsync(bill));
    }

    [Fact]
    public async Task InventoryCostCorrectionRequiresCurrentHeldStateFromItsProducer()
    {
        // GIVEN inventory recognized and invoiced through real source owners without current held-state proof.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        await context.EnableBillCorrectionsAsync();
        var bill = await context.ExpenseBillAsync("100", classification: "Inventory");
        var replacement = await context.BillReplacementAsync(bill, "90");
        var command = await context.CorrectionCommandAsync("Bill", bill, replacement);
        command["sourceDecisions"]!.AsArray().Add(await context.MatchedReceiptDecisionAsync(bill, replacement));
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);
        // WHEN invoice-first successor composition would reduce inventory cost THEN current producer state is mandatory.
        Assert.Equal(51004, (await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Bill", command))).Number);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
        Assert.Equal(100m, await context.Bills.Recognition.BalanceAsync("Inventory"));
    }

    [Fact]
    public async Task MatchedBillRequiresCurrentProducerAndPreservesWholeUnitLineage()
    {
        // GIVEN a real performed service matched to a real reviewed invoice and paid in full.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        await context.EnableBillCorrectionsAsync();
        var bill = await context.ExpenseBillAsync("300");
        var payment = await context.Payments.CommandAsync("300"); await context.Payments.AllocateAsync(payment, bill, "300");
        await context.Payments.RecordAsync(payment);
        var cash = await context.PaymentCashEvidenceAsync(); var source = await context.BillSourceEvidenceAsync(bill);
        var replacement = await context.BillReplacementAsync(bill, "280");
        var successor = Guid.Parse(replacement["post"]!["billId"]!.ToString());
        var command = await context.CorrectionCommandAsync("Bill", bill, replacement);
        command["sourceDecisions"]!.AsArray().Add(await context.MatchedReceiptDecisionAsync(bill, replacement));
        // WHEN the current source producer authorizes the published successor alongside the reviewed bill.
        await context.CaptureBillCorrectionPlanAsync(command); var result = await context.CorrectAsync("Bill", command);
        var group = Guid.Parse(result["groupId"]!.ToString());
        // THEN both original sides survive unchanged and the fresh whole unit carries both replacement links.
        Assert.Equal(cash, await context.PaymentCashEvidenceAsync()); Assert.Equal(source, await context.BillSourceEvidenceAsync(bill));
        Assert.Equal(0m, await context.BalanceAsync(bill)); Assert.Equal(280m, await context.BalanceAsync(successor));
        Assert.Equal(300m, await context.BalanceAsync(Guid.Parse(payment["paymentId"]!.ToString())));
        Assert.Equal(280m, await context.Bills.Recognition.BalanceAsync("Expense"));
        Assert.Equal(0m, await context.Bills.Recognition.BalanceAsync("Prepayment"));
        Assert.Equal(2, await context.Bills.ScalarAsync<int>($"SELECT COUNT(*) FROM Purchasing.RecognitionEventCorrections link JOIN Purchasing.SupplierSourceCorrectionMembers member ON member.TenantId=link.TenantId AND member.RecognitionEventId=link.OriginalEventId WHERE member.GroupId='{group}' AND member.Kind='ReverseSource' AND link.ReplacementEventId IS NOT NULL"));
        Assert.Equal(1m, await context.Bills.ScalarAsync<decimal>($"SELECT unit.Quantity FROM Purchasing.RecognitionUnits unit WHERE unit.Id='{replacement["unitBindings"]![0]!["successorUnitId"]}'"));
        await SupplierCorrectionFixture.AssertAttributionAsync(context.Payments);
    }

    [Fact]
    public async Task BillOwnerCannotUseStoredReceiptInsteadOfCurrentProducerAuthority()
    {
        // GIVEN a matched bill and a genuine prepared upstream revision.
        await using var context = await SupplierCreditTestContext.OpenAsync(sqlServer);
        await context.EnableBillCorrectionsAsync();
        var bill = await context.ExpenseBillAsync("100");
        var replacement = await context.BillReplacementAsync(bill, "90");
        var command = await context.CorrectionCommandAsync("Bill", bill, replacement);
        command["sourceDecisions"]!.AsArray().Add(await context.MatchedReceiptDecisionAsync(bill, replacement));
        var before = await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments);
        // WHEN current upstream correction authority is removed THEN stored evidence cannot authorize preview.
        await context.Bills.AdminAsync("DELETE FROM [Identity].RoleClaims WHERE ClaimType='workbench/permission' AND ClaimValue='PurchaseRecognitionFixtureCorrect'");
        Assert.Equal(51003, (await Assert.ThrowsAsync<SqlException>(() => context.PreviewCorrectionAsync("Bill", command))).Number);
        Assert.Equal(before, await SupplierOpenItemAtomicityTests.SnapshotAsync(context.Payments));
    }
}
