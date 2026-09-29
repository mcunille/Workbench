// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierBillPostingTests(SqlServerFixture sqlServer)
{
    internal static async Task<SupplierBillTestContext> OpenAsync(SqlServerFixture fixture, string? priorMigration = null)
    {
        var context = await SupplierBillTestContext.OpenAsync(fixture, priorMigration);
        await context.AdminAsync("""
            GRANT EXECUTE ON Purchasing.ReviewSupplierBill TO workbench_web;
            GRANT EXECUTE ON Purchasing.PostSupplierBill TO workbench_web;
            """);
        return context;
    }
    internal static async Task<JsonObject> ReviewedAsync(SupplierBillTestContext context, JsonObject? draft = null)
    {
        var saved = await context.SaveAsync(Guid.NewGuid(), draft ?? context.CompleteDraft());
        return await context.ReviewAsync(Guid.NewGuid(), context.ReviewCommand(saved));
    }
    [Fact]
    public async Task ReviewedInvoiceFirstPostsStoredComponentsOnce()
    {
        // GIVEN an approved invoice for cost 100 and recoverable tax 10.
        await using var context = await OpenAsync(sqlServer);
        var reviewed = await ReviewedAsync(context); var request = Guid.NewGuid(); var input = context.PostCommand(reviewed);
        // WHEN it posts and its identical request is retried.
        var posted = await context.ExecuteAsync("PostSupplierBill", request, input);
        Assert.Equal("Posted", posted["state"]?.GetValue<string>());
        var replay = await context.ExecuteAsync("PostSupplierBill", request, input);
        // THEN source, posting and retry identity describe one complete financial result.
        Assert.Equal(posted.ToJsonString(), replay.ToJsonString());
        Assert.Single(posted["journalIds"]!.AsArray());
        Assert.Single(posted["recognitionEventIds"]!.AsArray());
        Assert.Equal(100m, await context.Recognition.BalanceAsync("Prepayment"));
        Assert.Equal(10m, await context.Recognition.BalanceAsync("RecoverableTax"));
        Assert.Equal(-110m, await context.Recognition.BalanceAsync("SupplierPayable"));
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), input))).Number);
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.SaveAsync(Guid.NewGuid(), context.Change(posted, "Abandon")))).Number);
    }
    [Fact]
    public async Task ReceiptFirstInvoiceClearsAccrualWithoutDoubleRecognition()
    {
        // GIVEN a trusted receipt side for the exact source unit.
        await using var context = await OpenAsync(sqlServer);
        var recognition = await context.Recognition.CommandAsync();
        await context.Recognition.PostAsync(recognition.ToJsonString());
        var draft = context.CompleteDraft();
        draft["revision"]!["units"]![0]!["unitId"] = recognition["units"]![0]!["unitId"]!.DeepClone();
        draft["revision"]!["units"]![0]!["expectedPriorEventRevision"] = 1;
        var reviewed = await ReviewedAsync(context, draft);
        // WHEN the invoice posts THEN it clears the original accrual and leaves the recognized cost once.
        await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
        Assert.Equal(-110m, await context.Recognition.BalanceAsync("SupplierPayable"));
        Assert.Equal(0m, await context.Recognition.BalanceAsync("GoodsReceivedNotInvoiced"));
        Assert.Equal(100m, await context.Recognition.BalanceAsync("Inventory"));
        Assert.Equal(1, await context.Recognition.CountAsync("RecognitionMatches"));
    }
    [Theory]
    [InlineData("Inventory", false)]
    [InlineData("Expense", false)]
    [InlineData("Expense", true)]
    public async Task MatchedVarianceUsesOriginalClassification(string classification, bool variance)
    {
        // GIVEN recognized cost 100 and an invoice for the same stable unit.
        await using var context = await OpenAsync(sqlServer);
        var receipt = await context.Recognition.CommandAsync(classification: classification);
        await context.Recognition.PostAsync(receipt.ToJsonString());
        var draft = context.CompleteDraft(); var unit = draft["revision"]!["units"]![0]!;
        unit["classification"] = classification;
        unit["unitId"] = receipt["units"]![0]!["unitId"]!.DeepClone();
        unit["expectedPriorEventRevision"] = 1;
        if (variance)
        {
            unit["components"]![0]!["amount"] = "105.00"; draft["revision"]!["total"] = "115.00";
            unit["evidence"]!["varianceAmount"] = "5.00";
            unit["evidence"]!["varianceReason"] = "Final supplier price";
            unit["evidence"]!["varianceClassification"] = classification;
        }
        var reviewed = await ReviewedAsync(context, draft);
        // WHEN the reviewed invoice posts THEN the original classification owns any cost variance.
        await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
        Assert.Equal(variance ? 105m : 100m, await context.Recognition.BalanceAsync(classification));
        Assert.Equal(0m, await context.Recognition.BalanceAsync("GoodsReceivedNotInvoiced"));
    }

    [Fact]
    public async Task InventoryVarianceWithoutHeldStateFailsClosed()
    {
        // GIVEN inventory recognized at 100 and an invoice asserting an unsupported revaluation.
        await using var context = await OpenAsync(sqlServer);
        var receipt = await context.Recognition.CommandAsync(); await context.Recognition.PostAsync(receipt.ToJsonString());
        var draft = context.CompleteDraft(); var unit = draft["revision"]!["units"]![0]!;
        unit["unitId"] = receipt["units"]![0]!["unitId"]!.DeepClone(); unit["expectedPriorEventRevision"] = 1;
        unit["components"]![0]!["amount"] = "105.00"; draft["revision"]!["total"] = "115.00";
        unit["evidence"]!["varianceAmount"] = "5.00"; unit["evidence"]!["varianceReason"] = "Price change";
        unit["evidence"]!["varianceClassification"] = "Inventory";
        var reviewed = await ReviewedAsync(context, draft);
        // WHEN posting lacks authoritative held-inventory evidence THEN neither bill nor payable posts.
        Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed)))).Number);
        Assert.Equal(0m, await context.Recognition.BalanceAsync("SupplierPayable"));
        Assert.Equal(100m, await context.Recognition.BalanceAsync("Inventory"));
    }

    [Fact]
    public async Task ZeroAndMixedUnitsRetainEvidenceWithoutZeroOnlyJournals()
    {
        // GIVEN an invoice containing both classifications and a zero-valued unit.
        await using var context = await OpenAsync(sqlServer);
        var draft = context.CompleteDraft(); var units = draft["revision"]!["units"]!.AsArray();
        var expense = units[0]!.DeepClone(); expense["unitId"] = Guid.NewGuid().ToString();
        expense["componentKey"] = "expense"; expense["classification"] = "Expense"; units.Add(expense);
        var zero = units[0]!.DeepClone(); zero["unitId"] = Guid.NewGuid().ToString(); zero["componentKey"] = "free";
        zero["components"]![0]!["amount"] = "0"; zero["components"]![1]!["amount"] = "0"; units.Add(zero);
        draft["revision"]!["total"] = "220.00";
        var reviewed = await ReviewedAsync(context, draft);
        // WHEN the adapter posts THEN every source event survives and no zero-only journal appears.
        var posted = await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
        Assert.Equal(3, posted["recognitionEventIds"]!.AsArray().Count);
        Assert.Equal(2, posted["journalIds"]!.AsArray().Count);
        Assert.Equal(-220m, await context.Recognition.BalanceAsync("SupplierPayable"));
        Assert.Equal(200m, await context.Recognition.BalanceAsync("Prepayment"));
    }

    [Theory]
    [InlineData("ProForma")]
    [InlineData("Unreviewed")]
    [InlineData("StaleVersion")]
    [InlineData("CallerAmount")]
    [InlineData("ChangedDuplicateSet")]
    public async Task InvalidPostLeavesReviewedSourceAndNoJournal(string scenario)
    {
        // GIVEN a reviewed source which is non-posting or whose command/evidence changed.
        await using var context = await OpenAsync(sqlServer);
        var draft = context.CompleteDraft(kind: scenario == "ProForma" ? "ProForma" : "Invoice");
        var reviewed = scenario == "Unreviewed" ? await context.SaveAsync(Guid.NewGuid(), draft) : await ReviewedAsync(context, draft);
        var input = context.PostCommand(reviewed);
        if (scenario == "StaleVersion") input["billVersion"] = "0x0000000000000000";
        if (scenario == "CallerAmount") input["total"] = "1.00";
        if (scenario == "ChangedDuplicateSet") await context.SaveAsync(Guid.NewGuid(), context.CompleteDraft());
        // WHEN posting is attempted THEN it fails without partial financial state.
        var error = await Assert.ThrowsAsync<SqlException>(() => context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), input));
        Assert.Equal(scenario is "ProForma" or "CallerAmount" ? 51000 : 51009, error.Number);
        Assert.Equal(0, await context.Journal.CountAsync("JournalEntries"));
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM Purchasing.SupplierBills WHERE State='Posted'"));
    }
}
