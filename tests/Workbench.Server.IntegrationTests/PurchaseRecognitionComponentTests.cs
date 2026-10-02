// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class PurchaseRecognitionComponentTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData("Inventory")]
    [InlineData("Expense")]
    public async Task InvoiceBreakdownPostsNetCostAndSeparateRecoverableTax(string classification)
    {
        // GIVEN base 100, discount 10, charge 5, nonrecoverable tax 3 and recoverable tax 2.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var command = await context.CommandAsync("Invoice", classification, "98", "2");
        var side = command["units"]![0]!["sides"]![0]!;
        side["components"] = JsonNode.Parse("""
            [{"componentKey":"base","kind":"BaseCost","amount":"100"},
             {"componentKey":"discount","kind":"Discount","amount":"10","assignedCostComponentKey":"base"},
             {"componentKey":"charge","kind":"Charge","amount":"5","assignedCostComponentKey":"base","reason":"Supplier handling"},
             {"componentKey":"nonrecoverable","kind":"NonrecoverableTax","amount":"3","assignedCostComponentKey":"base","reason":"Tax policy"},
             {"componentKey":"recoverable","kind":"RecoverableTax","amount":"2"}]
            """);
        // WHEN the reviewed invoice posts through the durable source adapter.
        var result = await context.PostAsync(command.ToJsonString());
        // THEN the persisted journal recognizes cost 98, tax 2 and payable 100.
        Assert.Single(result.JournalIds);
        Assert.Equal(98m, await context.BalanceAsync("Prepayment"));
        Assert.Equal(2m, await context.BalanceAsync("RecoverableTax"));
        Assert.Equal(-100m, await context.BalanceAsync("SupplierPayable"));
        // AND invoice-first recognition does not yet debit inventory or expense.
        Assert.Equal(0m, await context.BalanceAsync(classification));
    }

    [Fact]
    public async Task FreightIsAssignedIntoClassifiedCost()
    {
        // GIVEN supplier freight assigned to a named base component of an eligible invoice.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var command = await context.CommandAsync("Invoice", cost: "100");
        command["units"]![0]!["sides"]![0]!["components"] = JsonNode.Parse("""
            [{"componentKey":"base","kind":"BaseCost","amount":"98"},
             {"componentKey":"freight","kind":"Freight","amount":"2","assignedCostComponentKey":"base","reason":"Supplier freight"}]
            """);
        // WHEN it posts THEN prepayment and payable include the freight in the classified cost.
        await context.PostAsync(command.ToJsonString());
        Assert.Equal(100m, await context.BalanceAsync("Prepayment"));
        Assert.Equal(-100m, await context.BalanceAsync("SupplierPayable"));
    }

    [Theory]
    [InlineData("0.02", false)]
    [InlineData("-0.01", true)]
    public async Task InvoiceRoundingHasOneMinorUnitBound(string adjustment, bool accepted)
    {
        // GIVEN an invoice cost adjusted from base 100 by an explicitly assigned rounding component.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var cost = (100m + decimal.Parse(adjustment, System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var command = await context.CommandAsync("Invoice", cost: cost);
        command["units"]![0]!["sides"]![0]!["components"] = JsonNode.Parse($$"""
            [{"componentKey":"base","kind":"BaseCost","amount":"100"},
             {"componentKey":"rounding","kind":"Rounding","amount":"{{adjustment}}","assignedCostComponentKey":"base","reason":"Approved invoice rounding"}]
            """);
        // WHEN posting THEN one signed minor unit succeeds, while two fail before receipt or journal creation.
        if (accepted)
        {
            await context.PostAsync(command.ToJsonString());
            Assert.Equal(100m + decimal.Parse(adjustment, System.Globalization.CultureInfo.InvariantCulture), await context.BalanceAsync("Prepayment"));
        }
        else
        {
            Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(command.ToJsonString()))).Number);
            Assert.Equal(0, await context.CountAsync("RecognitionGroupReceipts"));
            Assert.Equal(0, await context.Journal.CountAsync("JournalEntries"));
        }
    }

    [Theory]
    [InlineData("0.01", "50.01")]
    [InlineData("-0.01", "49.99")]
    public async Task RoundingBoundIsPerInvoiceAcrossUnits(string secondRounding, string secondGross)
    {
        // GIVEN two durable lines of the same invoice and a first approved +0.01 adjustment.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var first = await context.CommandAsync("Invoice", cost: "101");
        var firstSide = first["units"]![0]!["sides"]![0]!;
        firstSide["sourceAmount"] = "50.01";
        firstSide["components"] = RoundingComponents("0.01");
        var second = JsonNode.Parse(first.ToJsonString())!.AsObject();
        second["units"]![0]!["unitId"] = Guid.NewGuid();
        var secondSide = second["units"]![0]!["sides"]![0]!;
        secondSide["sourceComponentKey"] = "line-2";
        secondSide["subdivisionKey"] = "second";
        secondSide["sourceAmount"] = secondGross;
        secondSide["components"] = RoundingComponents(secondRounding);
        await using (var admin = new SqlConnection(context.Journal.Application.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var insert = new SqlCommand("""
                INSERT Purchasing.FixtureRecognitionSources(TenantId,Id,Revision,PurchaseOrderId,SupplierId,Currency,Classification,Side,SourceComponentKey,EvidenceJson)
                SELECT TenantId,Id,Revision,PurchaseOrderId,SupplierId,Currency,Classification,Side,N'line-2',EvidenceJson
                FROM Purchasing.FixtureRecognitionSources WHERE Id=@source AND SourceComponentKey=N'line-1'
                """, admin);
            insert.Parameters.AddWithValue("@source", Guid.Parse(firstSide["sourceId"]!.GetValue<string>()));
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        }
        await context.PostAsync(first.ToJsonString());
        // WHEN another subdivision adds or offsets rounding THEN identity and the invoice-wide bound reject it.
        Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(second.ToJsonString()))).Number);
        Assert.Equal(1, await context.CountAsync("RecognitionSideEvents"));
        Assert.Equal(1, await context.CountAsync("RecognitionGroupReceipts"));
        Assert.Equal(1, await context.Journal.CountAsync("JournalEntries"));
    }

    [Fact]
    public async Task TwoSubdivisionsExhaustDurableSourceCapacity()
    {
        // GIVEN a source with capacity one unit and 100 gross, allocated to two explicit halves.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var first = await context.CommandAsync(cost: "100");
        var firstUnit = first["units"]![0]!; var firstSide = firstUnit["sides"]![0]!;
        firstUnit["quantity"] = "0.5"; firstSide["sourceQuantity"] = "0.5"; firstSide["sourceAmount"] = "50";
        firstSide["components"]![0]!["amount"] = "50";
        var second = JsonNode.Parse(first.ToJsonString())!.AsObject();
        second["units"]![0]!["unitId"] = Guid.NewGuid();
        second["units"]![0]!["sides"]![0]!["subdivisionKey"] = "half-2";
        // WHEN both halves post THEN their persisted source claims consume the capacity.
        await context.PostAsync(first.ToJsonString());
        await context.PostAsync(second.ToJsonString());
        Assert.Equal(2, await context.CountAsync("RecognitionSideEvents"));
        // AND a third subdivision cannot gain capacity through a fresh unit ID.
        var third = JsonNode.Parse(first.ToJsonString())!.AsObject();
        third["units"]![0]!["unitId"] = Guid.NewGuid();
        third["units"]![0]!["sides"]![0]!["subdivisionKey"] = "half-3";
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(third.ToJsonString()))).Number);
        // AND contradictory capacity evidence cannot replace the durable source record.
        third["units"]![0]!["sides"]![0]!["evidence"]!["sourceCapacityAmount"] = "200";
        Assert.Equal(51004, (await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(third.ToJsonString()))).Number);
        Assert.Equal(2, await context.CountAsync("RecognitionSideEvents"));
        Assert.Equal(2, await context.CountAsync("RecognitionGroupReceipts"));
        Assert.Equal(2, await context.Journal.CountAsync("JournalEntries"));
    }

    [Theory]
    [InlineData(0, "101", "1")]
    [InlineData(1, "100.1", "0.1")]
    [InlineData(2, "100.01", "0.01")]
    [InlineData(3, "100.001", "0.001")]
    [InlineData(4, "100.0001", "0.0001")]
    public async Task InvoiceRoundingUsesConfiguredMinorUnitAtEverySupportedScale(int scale, string gross, string adjustment)
    {
        // GIVEN a configured functional currency scale and its exact minor unit.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        await SetScaleAsync(context, scale);
        var command = await context.CommandAsync("Invoice", cost: gross);
        command["units"]![0]!["sides"]![0]!["components"] = JsonNode.Parse($$"""
            [{"componentKey":"base","kind":"BaseCost","amount":"100"},
             {"componentKey":"rounding","kind":"Rounding","amount":"{{adjustment}}","assignedCostComponentKey":"base","reason":"Approved invoice rounding"}]
            """);
        // WHEN posting THEN the exact configured minor unit remains in payable and prepayment.
        await context.PostAsync(command.ToJsonString());
        Assert.Equal(decimal.Parse(gross, System.Globalization.CultureInfo.InvariantCulture), await context.BalanceAsync("Prepayment"));
        Assert.Equal(-decimal.Parse(gross, System.Globalization.CultureInfo.InvariantCulture), await context.BalanceAsync("SupplierPayable"));
    }

    [Theory]
    [InlineData("duplicate-rounding,negative-cost,excess-precision,overflow,unknown-nested,duplicate-nested,negative-assigned-cost")]
    [InlineData("fractional-negative-assigned-cost")]
    public async Task InvalidBreakdownCannotCreateFinancialEvidence(string defects)
    {
        // GIVEN a durable source and a breakdown that violates one typed component rule.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        foreach (var defect in defects.Split(','))
        {
            var command = await context.CommandAsync("Invoice", cost: defect == "negative-cost" ? "1" :
                defect == "overflow" ? "999999999999999999999999.99" : defect == "negative-assigned-cost" ? "2" :
                defect == "fractional-negative-assigned-cost" ? "0.9999" : "100");
            if (defect == "fractional-negative-assigned-cost") await SetScaleAsync(context, 4);
            var side = command["units"]![0]!["sides"]![0]!;
            string? json = null;
            switch (defect)
            {
                case "duplicate-rounding":
                    side["components"] = JsonNode.Parse("""
                    [{"componentKey":"base","kind":"BaseCost","amount":"100"},
                     {"componentKey":"plus","kind":"Rounding","amount":"0.01","assignedCostComponentKey":"base","reason":"Approved"},
                     {"componentKey":"minus","kind":"Rounding","amount":"-0.01","assignedCostComponentKey":"base","reason":"Approved"}]
                    """); break;
                case "negative-cost":
                    side["components"] = JsonNode.Parse("""
                    [{"componentKey":"base","kind":"BaseCost","amount":"1"},
                     {"componentKey":"discount","kind":"Discount","amount":"2","assignedCostComponentKey":"base"}]
                    """); break;
                case "excess-precision": side["components"]![0]!["amount"] = "100.00001"; break;
                case "overflow":
                    side["components"] = JsonNode.Parse("""
                    [{"componentKey":"base","kind":"BaseCost","amount":"999999999999999999999999.99"},
                     {"componentKey":"charge","kind":"Charge","amount":"1","assignedCostComponentKey":"base","reason":"Supplier handling"}]
                    """); break;
                case "unknown-nested": side["components"]![0]!["ledgerAccountId"] = Guid.NewGuid(); break;
                case "duplicate-nested":
                    json = command.ToJsonString().Replace("\"kind\":\"BaseCost\"", "\"kind\":\"BaseCost\",\"kind\":\"BaseCost\"", StringComparison.Ordinal); break;
                case "negative-assigned-cost":
                    side["components"] = JsonNode.Parse("""
                    [{"componentKey":"small-base","kind":"BaseCost","amount":"1"},
                     {"componentKey":"large-base","kind":"BaseCost","amount":"3"},
                     {"componentKey":"discount","kind":"Discount","amount":"2","assignedCostComponentKey":"small-base"}]
                    """); break;
                case "fractional-negative-assigned-cost":
                    side["components"] = JsonNode.Parse("""
                    [{"componentKey":"small-base","kind":"BaseCost","amount":"1.0000"},
                     {"componentKey":"other-base","kind":"BaseCost","amount":"1.0000"},
                     {"componentKey":"discount","kind":"Discount","amount":"1.0001","assignedCostComponentKey":"small-base"}]
                    """); break;
            }
            // WHEN posting THEN malformed, nonrepresentable or invalid allocation is rejected atomically.
            var error = await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(json ?? command.ToJsonString()));
            Assert.True(error.Number == 51000, $"{defect}: expected structural rejection, got {error.Number}.");
            if (defect == "fractional-negative-assigned-cost") Assert.Contains("Assigned cost component cannot become negative", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, await context.CountAsync("RecognitionSideEvents"));
            Assert.Equal(0, await context.CountAsync("RecognitionGroupReceipts"));
            Assert.Equal(0, await context.Journal.CountAsync("JournalEntries"));
        }
        // AND rejection does not poison the context: a complete invoice can still commit.
        var valid = await context.CommandAsync("Invoice");
        var posted = await context.PostAsync(valid.ToJsonString());
        Assert.Single(posted.JournalIds);
        Assert.Equal(1, await context.CountAsync("RecognitionSideEvents"));
        Assert.Equal(1, await context.CountAsync("RecognitionGroupReceipts"));
    }

    [Fact]
    public async Task RecoverableTaxRequiresDurableApprovedEntitlement()
    {
        // GIVEN a durable invoice with a recoverable amount but no tax entitlement.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer);
        var command = await context.CommandAsync("Invoice", cost: "98", tax: "2");
        var side = command["units"]![0]!["sides"]![0]!;
        side["evidence"]!["taxEntitlement"] = false;
        await using (var admin = new SqlConnection(context.Journal.Application.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var update = new SqlCommand("UPDATE Purchasing.FixtureRecognitionSources SET EvidenceJson=@evidence WHERE Id=@source", admin);
            update.Parameters.AddWithValue("@evidence", side["evidence"]!.ToJsonString());
            update.Parameters.AddWithValue("@source", Guid.Parse(side["sourceId"]!.GetValue<string>()));
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }
        // WHEN the invoice posts THEN SQL rejects the unsupported recoverable tax with no receipt.
        Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => context.PostAsync(command.ToJsonString()))).Number);
        Assert.Equal(0, await context.CountAsync("RecognitionSideEvents"));
        Assert.Equal(0, await context.CountAsync("RecognitionGroupReceipts"));
    }

    private static async Task SetScaleAsync(PurchaseRecognitionTestContext context, int scale)
    {
        await using var admin = new SqlConnection(context.Journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        await using var update = new SqlCommand("UPDATE Accounting.Configurations SET Payload=JSON_MODIFY(Payload,'$.policies.scale',@scale)", admin);
        update.Parameters.AddWithValue("@scale", scale);
        await update.ExecuteNonQueryAsync();
    }

    private static JsonNode RoundingComponents(string adjustment) => JsonNode.Parse($$"""
        [{"componentKey":"base","kind":"BaseCost","amount":"50"},
         {"componentKey":"rounding","kind":"Rounding","amount":"{{adjustment}}","assignedCostComponentKey":"base","reason":"Approved invoice rounding"}]
        """)!;
}
