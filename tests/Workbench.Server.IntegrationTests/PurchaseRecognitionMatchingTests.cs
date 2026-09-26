// Copyright (c) 2026 The White Stag Collection.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class PurchaseRecognitionMatchingTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData("Inventory", "100", "105", "5", 105, -110)]
    [InlineData("Inventory", "100", "98", "0", 98, -98)]
    [InlineData("Expense", "100", "105", "5", 105, -110)]
    [InlineData("Expense", "0", "105", "0", 105, -105)]
    [InlineData("Inventory", "0", "105", "0", 105, -105)]
    [InlineData("Inventory", "100", "0", "0", 0, 0)]
    [InlineData("Inventory", "0", "0", "0", 0, 0)]
    public async Task InvoiceClearsAccrualAndPostsOnlyDifference(string classification, string estimate, string cost, string tax, decimal expectedCost, decimal expectedPayable)
    {
        // GIVEN a recognized whole unit, with reviewed variance and durable held-inventory evidence.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var receipt = await context.CommandAsync(classification: classification, cost: estimate);
        var first = await context.PostAsync(receipt.ToJsonString());
        var invoice = await context.CommandAsync("Invoice", classification, cost, tax);
        Link(invoice, receipt);
        await ApproveVarianceAsync(context, invoice, decimal.Parse(cost, CultureInfo.InvariantCulture) - decimal.Parse(estimate, CultureInfo.InvariantCulture), classification);
        var oldAccrual = context.Accounts["GoodsReceivedNotInvoiced"];
        var oldCost = context.Accounts[classification];
        await RemapAsync(context, "GoodsReceivedNotInvoiced");
        if (classification == "Expense") await RemapAsync(context, "Expense");
        // WHEN the invoice is posted after remapping THEN original accrual clears and cost is adjusted once.
        var matched = await context.PostAsync(invoice.ToJsonString());
        Assert.Single(matched.MatchIds);
        Assert.Equal(0m, await BalanceAsync(context, oldAccrual));
        Assert.Equal(expectedCost, await BalanceAsync(context, oldCost));
        Assert.Equal(0m, await context.BalanceAsync("GoodsReceivedNotInvoiced"));
        Assert.Equal(decimal.Parse(tax, CultureInfo.InvariantCulture), await context.BalanceAsync("RecoverableTax"));
        Assert.Equal(expectedPayable, await context.BalanceAsync("SupplierPayable"));
        Assert.Equal(0m, await context.BalanceAsync("Prepayment"));
        if (classification == "Expense") Assert.Equal(0m, await context.BalanceAsync("Expense"));
        await AssertMatchAsync(context, matched.MatchIds[0], first.EventIds[0], matched.EventIds[0]);
    }

    [Theory]
    [InlineData("105", "5", "100", 105, -110)]
    [InlineData("0", "0", "100", 0, 0)]
    [InlineData("105", "5", "0", 105, -110)]
    public async Task RecognitionClearsOriginalPrepayment(string cost, string tax, string estimate, decimal expectedCost, decimal expectedPayable)
    {
        // GIVEN an invoice-first unit whose prepayment mapping subsequently changes.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var invoice = await context.CommandAsync("Invoice", cost: cost, tax: tax);
        var first = await context.PostAsync(invoice.ToJsonString());
        var originalPrepayment = context.Accounts["Prepayment"];
        await RemapAsync(context, "Prepayment");
        var receipt = await context.CommandAsync(cost: estimate);
        Link(receipt, invoice);
        // WHEN recognition occurs THEN final invoice cost is recognized and the historical account clears.
        var result = await context.PostAsync(receipt.ToJsonString());
        Assert.Equal(expectedCost, await context.BalanceAsync("Inventory"));
        Assert.Equal(expectedPayable, await context.BalanceAsync("SupplierPayable"));
        Assert.Equal(decimal.Parse(tax, CultureInfo.InvariantCulture), await context.BalanceAsync("RecoverableTax"));
        Assert.Equal(0m, await BalanceAsync(context, originalPrepayment));
        Assert.Equal(0m, await context.BalanceAsync("Prepayment"));
        Assert.Equal(0m, await context.BalanceAsync("GoodsReceivedNotInvoiced"));
        Assert.Equal(expectedCost == 0 ? 0 : 1, result.JournalIds.Length);
        await AssertMatchAsync(context, Assert.Single(result.MatchIds), result.EventIds[0], first.EventIds[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CombinedCommandHasOneRecordedInstantAndReplays(bool invoiceFirst)
    {
        // GIVEN both sides in one explicit unit and one command, in either input order.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var receipt = await context.CommandAsync();
        var invoice = await context.CommandAsync("Invoice", cost: "105", tax: "5");
        await ApproveVarianceAsync(context, invoice, 5, "Inventory");
        var command = invoiceFirst ? invoice : receipt;
        command["units"]![0]!["sides"]!.AsArray().Add((invoiceFirst ? receipt : invoice)["units"]![0]!["sides"]![0]!.DeepClone());
        var request = Guid.NewGuid();
        // WHEN both sides commit THEN balances and every new financial evidence timestamp agree.
        var result = await context.PostAsync(command.ToJsonString(), request);
        Assert.Equal(2, result.EventIds.Length); Assert.Equal(2, result.JournalIds.Length); Assert.Single(result.MatchIds);
        Assert.Equal(105m, await context.BalanceAsync("Inventory"));
        Assert.Equal(-110m, await context.BalanceAsync("SupplierPayable"));
        Assert.Equal(0m, await context.BalanceAsync("Prepayment"));
        Assert.Equal(0m, await context.BalanceAsync("GoodsReceivedNotInvoiced"));
        await using var times = new SqlCommand("""
            SELECT COUNT(*) FROM (
              SELECT RecordedAtUtc FROM Purchasing.RecognitionSideEvents UNION ALL SELECT RecordedAtUtc FROM Purchasing.RecognitionMatches
              UNION ALL SELECT RecordedAtUtc FROM Purchasing.RecognitionGroupReceipts UNION ALL SELECT RecordedAtUtc FROM Accounting.JournalEntries
              UNION ALL SELECT RecordedAtUtc FROM Accounting.SourceEvents UNION ALL SELECT RecordedAtUtc FROM Accounting.PostingReceipts
              UNION ALL SELECT OccurredAtUtc FROM Security.TenantSecurityAuditEvents WHERE Action IN ('Accounting.PostJournal','Purchasing.PostRecognition')
            ) t WHERE RecordedAtUtc<>@instant
            """, context.Connection);
        times.Parameters.AddWithValue("@instant", result.RecordedAtUtc);
        Assert.Equal(0, (int)(await times.ExecuteScalarAsync())!);
        var replay = await context.PostAsync(command.ToJsonString(), request);
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(replay));
        Assert.Equal(2, await context.CountAsync("RecognitionSideEvents"));
    }

    [Theory]
    [InlineData("quantity", 51009)]
    [InlineData("classification", 51009)]
    [InlineData("prior", 51009)]
    [InlineData("date", 51000)]
    [InlineData("variance", 51000)]
    [InlineData("disposed", 51000)]
    [InlineData("forged-held", 51004)]
    [InlineData("mapping", 51004)]
    public async Task IncompatibleSecondSideRollsBackAndValidSideCanStillMatch(string defect, int errorNumber)
    {
        // GIVEN a receipt and a valid invoice, with one incompatible claim selected below.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var receipt = await context.CommandAsync();
        await context.PostAsync(receipt.ToJsonString());
        var invoice = await context.CommandAsync("Invoice", classification: defect == "classification" ? "Expense" : "Inventory", cost: "105", tax: "5");
        Link(invoice, receipt);
        await ApproveVarianceAsync(context, invoice, 5, defect == "classification" ? "Expense" : "Inventory");
        var valid = invoice.ToJsonString();
        var unit = invoice["units"]![0]!; var side = unit["sides"]![0]!;
        switch (defect)
        {
            case "quantity": unit["quantity"] = "2"; side["sourceQuantity"] = "2"; break;
            case "classification": break;
            case "prior": unit["expectedPriorEventRevision"] = 2; break;
            case "date": invoice["postingDate"] = "2026-01-31"; side["effectiveDate"] = "2026-01-31"; break;
            case "variance": side["evidence"]!["varianceAmount"] = "4"; await StoreEvidenceAsync(context, side); break;
            case "disposed": side["evidence"]!["inventoryAdjustmentState"] = "Sold"; await StoreEvidenceAsync(context, side); break;
            case "forged-held": side["evidence"]!["inventoryAdjustmentState"] = "Sold"; await StoreEvidenceAsync(context, side); side["evidence"]!["inventoryAdjustmentState"] = "Held"; break;
            case "mapping": await SetMappingAsync(context, "SupplierPayable", Guid.NewGuid()); break;
        }
        // WHEN the incompatible side posts THEN no partial event, match, journal or receipt survives.
        Assert.Equal(errorNumber, (await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(invoice.ToJsonString()))).Number);
        Assert.Equal(1, await context.CountAsync("RecognitionSideEvents")); Assert.Equal(0, await context.CountAsync("RecognitionMatches"));
        Assert.Equal(1, await context.CountAsync("RecognitionGroupReceipts")); Assert.Equal(1, await context.Journal.CountAsync("JournalEntries"));
        // AND a corrected source can match, proving the failure did not poison the unit.
        if (defect == "classification")
        {
            invoice = await context.CommandAsync("Invoice", cost: "105", tax: "5"); Link(invoice, receipt);
            await ApproveVarianceAsync(context, invoice, 5, "Inventory"); valid = invoice.ToJsonString();
        }
        var restored = JsonNode.Parse(valid)!;
        await StoreEvidenceAsync(context, restored["units"]![0]!["sides"]![0]!);
        await SetMappingAsync(context, "SupplierPayable", context.Accounts["SupplierPayable"]);
        Assert.Single((await context.PostAsync(valid)).MatchIds);
    }

    [Fact]
    public async Task InvalidCombinedSecondSideRollsBackEveryNewFinancialRow()
    {
        // GIVEN two valid source identities with a reviewed variance that does not equal their difference.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var receipt = await context.CommandAsync();
        var invoice = await context.CommandAsync("Invoice", cost: "105");
        await ApproveVarianceAsync(context, invoice, 4, "Inventory");
        receipt["units"]![0]!["sides"]!.AsArray().Add(invoice["units"]![0]!["sides"]![0]!.DeepClone());
        // WHEN the second side rejects THEN the first journal, period, source event and unit also roll back.
        var error = await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(receipt.ToJsonString()));
        Assert.Equal(51000, error.Number); Assert.Contains("variance", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, await context.CountAsync("RecognitionUnits"));
        Assert.Equal(0, await context.CountAsync("RecognitionSideEvents"));
        Assert.Equal(0, await context.CountAsync("RecognitionMatches"));
        Assert.Equal(0, await context.CountAsync("RecognitionGroupReceipts"));
        Assert.Equal(0, await context.Journal.CountAsync("JournalEntries"));
        Assert.Equal(0, await context.Journal.CountAsync("SourceEvents"));
        Assert.Equal(0, await context.Journal.CountAsync("Periods"));
        // AND correcting that variance allows the entire command to commit.
        await ApproveVarianceAsync(context, invoice, 5, "Inventory");
        receipt["units"]![0]!["sides"]![1] = invoice["units"]![0]!["sides"]![0]!.DeepClone();
        Assert.Single((await context.PostAsync(receipt.ToJsonString())).MatchIds);
    }

    [Fact]
    public async Task EqualAmountsRemainIndependentUntilAnExplicitUnitMatch()
    {
        // GIVEN a receipt and invoice with equal amounts but distinct explicit unit identities.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var receipt = await context.CommandAsync();
        var invoice = await context.CommandAsync("Invoice");
        await context.PostAsync(receipt.ToJsonString());
        // WHEN the independent invoice posts THEN amount equality does not clear or infer a match.
        Assert.Empty((await context.PostAsync(invoice.ToJsonString())).MatchIds);
        Assert.Equal(100m, await context.BalanceAsync("Prepayment"));
        Assert.Equal(-100m, await context.BalanceAsync("GoodsReceivedNotInvoiced"));
        // AND an explicitly linked new invoice can match without variance approval when the difference is zero.
        var linkedInvoice = await context.CommandAsync("Invoice"); Link(linkedInvoice, receipt);
        Assert.Single((await context.PostAsync(linkedInvoice.ToJsonString())).MatchIds);
        Assert.Equal(0m, await context.BalanceAsync("GoodsReceivedNotInvoiced"));
        Assert.Equal(100m, await context.BalanceAsync("Inventory"));
        Assert.Equal(100m, await context.BalanceAsync("Prepayment"));
        Assert.Equal(-200m, await context.BalanceAsync("SupplierPayable"));
        // AND the same unit cannot silently acquire another active invoice under a fresh request.
        var duplicate = await context.CommandAsync("Invoice"); Link(duplicate, receipt);
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(duplicate.ToJsonString()))).Number);
        Assert.Equal(3, await context.CountAsync("RecognitionSideEvents"));
    }

    private static void Link(JsonObject second, JsonObject first)
    {
        second["units"]![0]!["unitId"] = first["units"]![0]!["unitId"]!.DeepClone();
        second["units"]![0]!["expectedPriorEventRevision"] = 1;
    }

    private static async Task ApproveVarianceAsync(PurchaseRecognitionTestContext context, JsonObject command, decimal amount, string classification)
    {
        var side = command["units"]![0]!["sides"]![0]!;
        side["evidence"]!["varianceAmount"] = amount.ToString(CultureInfo.InvariantCulture);
        side["evidence"]!["varianceReason"] = "Reviewed supplier price difference";
        side["evidence"]!["varianceClassification"] = classification;
        side["evidence"]!["inventoryAdjustmentState"] = "Held";
        await StoreEvidenceAsync(context, side);
    }

    private static async Task StoreEvidenceAsync(PurchaseRecognitionTestContext context, JsonNode side)
    {
        await using var admin = new SqlConnection(context.Journal.Application.AdminConnectionString); await admin.OpenAsync();
        await using var command = new SqlCommand("UPDATE Purchasing.FixtureRecognitionSources SET EvidenceJson=@evidence WHERE Id=@id AND Revision=@revision", admin);
        command.Parameters.AddWithValue("@evidence", side["evidence"]!.ToJsonString());
        command.Parameters.AddWithValue("@id", side["sourceId"]!.GetValue<string>());
        command.Parameters.AddWithValue("@revision", side["sourceRevision"]!.GetValue<string>());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RemapAsync(PurchaseRecognitionTestContext context, string slot)
    {
        var created = await context.Journal.SaveAsync(Guid.NewGuid(), "CreateAccounts", JsonSerializer.Serialize(new[] { new { code = "9" + context.Accounts.Count.ToString(CultureInfo.InvariantCulture), name = "Replacement " + slot, type = slot == "Expense" ? "Expense" : slot == "GoodsReceivedNotInvoiced" ? "Liability" : "Asset", purpose = "General" } }));
        var id = JsonSerializer.Deserialize<Guid[]>(created.Ids)![0];
        context.Accounts[slot + "Original"] = context.Accounts[slot]; context.Accounts[slot] = id;
        await SetMappingAsync(context, slot, id);
    }

    private static async Task SetMappingAsync(PurchaseRecognitionTestContext context, string slot, Guid id)
    {
        await using var admin = new SqlConnection(context.Journal.Application.AdminConnectionString); await admin.OpenAsync();
        await using var command = new SqlCommand("""
            DECLARE @path nvarchar(100)=(SELECT '$.mappings['+[key]+'].accountId' FROM Accounting.Configurations c CROSS APPLY OPENJSON(c.Payload,'$.mappings') WHERE c.TenantId=@tenant AND JSON_VALUE(value,'$.slot')=@slot);
            UPDATE Accounting.Configurations SET Payload=JSON_MODIFY(Payload,@path,CONVERT(nvarchar(36),@id)) WHERE TenantId=@tenant
            """, admin);
        command.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId); command.Parameters.AddWithValue("@slot", slot); command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<decimal> BalanceAsync(PurchaseRecognitionTestContext context, Guid account)
    {
        await using var command = new SqlCommand("SELECT COALESCE(SUM(Debit-Credit),0) FROM Accounting.JournalLines WHERE AccountId=@id", context.Connection);
        command.Parameters.AddWithValue("@id", account); return (decimal)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AssertMatchAsync(PurchaseRecognitionTestContext context, Guid match, Guid recognition, Guid invoice)
    {
        await using var command = new SqlCommand("SELECT COUNT(*) FROM Purchasing.RecognitionMatches WHERE Id=@id AND RecognitionEventId=@recognition AND InvoiceEventId=@invoice", context.Connection);
        command.Parameters.AddWithValue("@id", match); command.Parameters.AddWithValue("@recognition", recognition); command.Parameters.AddWithValue("@invoice", invoice);
        Assert.Equal(1, (int)(await command.ExecuteScalarAsync())!);
    }
}
