// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class PurchaseRecognitionPostingTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("duplicate-side")]
    [InlineData("wrong-type")]
    [InlineData("precision")]
    [InlineData("source-total")]
    [InlineData("quantity")]
    [InlineData("currency")]
    [InlineData("start-date")]
    [InlineData("enum-padding")]
    public async Task InvalidTypedInputLeavesNoFinancialState(string defect)
    {
        // GIVEN a durable source and an invalid financial envelope.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var node = await context.CommandAsync(cost: defect == "start-date" ? "0" : "100");
        var validJson = node.ToJsonString();
        var unit = node["units"]![0]!; var side = unit["sides"]![0]!;
        switch (defect)
        {
            case "unknown": unit["accountId"] = Guid.NewGuid(); break;
            case "duplicate-side": unit["sides"]!.AsArray().Add(side.DeepClone()); break;
            case "wrong-type": unit["quantity"] = 1; break;
            case "precision": side["components"]![0]!["amount"] = "100.001"; break;
            case "source-total": side["components"]![0]!["amount"] = "101"; break;
            case "quantity": side["sourceQuantity"] = "2"; break;
            case "currency": node["currency"] = "EUR"; break;
            case "start-date": node["postingDate"] = "2025-12-31"; break;
            case "enum-padding": side["components"]![0]!["kind"] = "BaseCost "; break;
        }
        var json = node.ToJsonString();
        if (defect == "duplicate") json = json.Replace("\"componentKey\":\"base\"", "\"componentKey\":\"base\",\"componentKey\":\"base\"", StringComparison.Ordinal);
        // WHEN posting THEN validation rejects without unit, event, receipt, journal or materialized period.
        var error = await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(json));
        Assert.Equal(defect == "currency" ? 51004 : 51000, error.Number);
        Assert.Equal(0, await context.CountAsync("RecognitionUnits")); Assert.Equal(0, await context.CountAsync("RecognitionGroupReceipts"));
        Assert.Equal(0, await context.Journal.CountAsync("JournalEntries")); Assert.Equal(0, await context.Journal.CountAsync("Periods"));
        // AND the unchanged source posts, proving rejection was caused by the selected defect.
        Assert.Single((await context.PostAsync(validJson)).EventIds);
    }

    [Fact]
    public async Task MaximumCurrencyAmountRemainsExactlyRepresentable()
    {
        // GIVEN the kernel's supported 24-digit integer range with two currency decimals.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var command = await context.CommandAsync(cost: "999999999999999999999999.99");
        // WHEN recognition posts THEN exact cost is preserved without an artificially smaller parser range.
        await context.PostAsync(command.ToJsonString());
        Assert.Equal(999999999999999999999999.99m, await context.BalanceAsync("Inventory"));
    }

    [Fact]
    public async Task SourceSubdivisionCannotIncreaseCapacityOrInventAComponent()
    {
        // GIVEN a whole source component already recognized.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var command = await context.CommandAsync();
        await context.PostAsync(command.ToJsonString());
        command["units"]![0]!["unitId"] = Guid.NewGuid();
        command["units"]![0]!["sides"]![0]!["subdivisionKey"] = "second";
        // WHEN a new subdivision exceeds aggregate source capacity THEN it conflicts.
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(command.ToJsonString()))).Number);
        // AND inventing a different source component cannot evade durable source authority.
        command["units"]![0]!["sides"]![0]!["sourceComponentKey"] = "invented";
        Assert.Equal(51004, (await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(command.ToJsonString()))).Number);
        Assert.Equal(1, await context.CountAsync("RecognitionSideEvents"));
    }

    [Fact]
    public async Task ReplayPrecedesMutableSourceAndConfigurationChecks()
    {
        // GIVEN a successfully receipted source transaction.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var command = await context.CommandAsync(); var request = Guid.NewGuid();
        var result = await context.PostAsync(command.ToJsonString(), request);
        await using var admin = new SqlConnection(context.Journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        await using (var change = new SqlCommand("UPDATE Accounting.Configurations SET Version=NEWID(); UPDATE Purchasing.FixtureRecognitionSources SET Revision=NEWID();", admin))
            await change.ExecuteNonQueryAsync();
        // WHEN the lost response is retried after upstream changes THEN every original result value is retained.
        var replay = await context.PostAsync(command.ToJsonString(), request);
        Assert.Equal(result.CommandId, replay.CommandId); Assert.Equal(result.UnitIds, replay.UnitIds); Assert.Equal(result.EventIds, replay.EventIds);
        Assert.Equal(result.JournalIds, replay.JournalIds); Assert.Equal(result.MatchIds, replay.MatchIds);
        Assert.Equal(result.CorrectionGroupId, replay.CorrectionGroupId); Assert.Equal(result.RecordedAtUtc, replay.RecordedAtUtc);
        // AND a changed valid envelope conflicts before stale mutable-source checks.
        command["postingDate"] = "2026-02-02";
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(command.ToJsonString(), request))).Number);
    }

    [Theory]
    [InlineData("Inventory", "ControlTransferred")]
    [InlineData("Expense", "ServicePerformed")]
    [InlineData("Expense", "GoodsConsumed")]
    public async Task ReceiptBeforeInvoiceAccruesCost(string classification, string recognitionBasis)
    {
        // GIVEN an approved whole-unit estimate of 100.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var command = await context.CommandAsync(classification: classification, recognitionBasis: recognitionBasis);
        // WHEN its recognition side posts through the durable source adapter.
        var result = await context.PostAsync(command.ToJsonString());
        // THEN cost is recognized against receipt accrual, without supplier AP.
        Assert.Single(result.JournalIds); Assert.Single(result.EventIds);
        Assert.Equal(100m, await context.BalanceAsync(classification));
        Assert.Equal(-100m, await context.BalanceAsync("GoodsReceivedNotInvoiced"));
        Assert.Equal(0m, await context.BalanceAsync("SupplierPayable"));
    }
    [Theory]
    [InlineData("Inventory")]
    [InlineData("Expense")]
    public async Task EligibleInvoiceCreatesPrepayment(string classification)
    {
        // GIVEN a present obligation and enforceable future right with cost 105 and recoverable tax 5.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var command = await context.CommandAsync("Invoice", classification, "105", "5");
        // WHEN the invoice arrives before recognition.
        var result = await context.PostAsync(command.ToJsonString());
        // THEN independent persisted balances are prepayment 105, tax 5 and AP 110.
        Assert.Single(result.JournalIds);
        Assert.Equal(105m, await context.BalanceAsync("Prepayment")); Assert.Equal(5m, await context.BalanceAsync("RecoverableTax"));
        Assert.Equal(-110m, await context.BalanceAsync("SupplierPayable")); Assert.Equal(0m, await context.BalanceAsync(classification));
    }
    [Fact]
    public async Task ProFormaCannotPost()
    {
        // GIVEN durable evidence that does not establish invoice eligibility.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var command = await context.CommandAsync("Invoice", eligible: false);
        // WHEN a source attempts financial posting THEN it fails without partial financial evidence.
        Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(command.ToJsonString()))).Number);
        Assert.Equal(0, await context.CountAsync("RecognitionSideEvents")); Assert.Equal(0, await context.CountAsync("RecognitionGroupReceipts"));
        Assert.Equal(0, await context.Journal.CountAsync("JournalEntries"));
    }
    [Fact]
    public async Task ZeroValueSideKeepsEvidenceWithoutJournal()
    {
        // GIVEN a zero-valued recognition with a durable source identity.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var command = await context.CommandAsync(cost: "0"); var request = Guid.NewGuid();
        // WHEN posting and retrying the same command.
        var result = await context.PostAsync(command.ToJsonString(), request);
        var replay = await context.PostAsync(command.ToJsonString(), request);
        // THEN evidence and receipt are unique, replay preserves every identifier, and another request cannot duplicate the source.
        Assert.Empty(result.JournalIds); Assert.Equal(result.CommandId, replay.CommandId); Assert.Equal(result.EventIds, replay.EventIds);
        Assert.Equal(result.UnitIds, replay.UnitIds); Assert.Equal(result.RecordedAtUtc, replay.RecordedAtUtc);
        Assert.Equal(1, await context.CountAsync("RecognitionSideEvents")); Assert.Equal(1, await context.CountAsync("RecognitionGroupReceipts"));
        command["units"]![0]!["unitId"] = Guid.NewGuid();
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(command.ToJsonString()))).Number);
    }
}
