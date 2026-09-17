// Copyright (c) 2026 The White Stag Collection.
using Microsoft.AspNetCore.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Server.Purchasing;
using Xunit;
namespace Workbench.Server.IntegrationTests;

public sealed class DraftAdjustmentsTests
{
    [Fact]
    public void WorkedExampleCalculatesDiscountsAndIndependentPayees()
    {
        // GIVEN merchandise, line and order discounts, supplier charges and a bank fee.
        var node = JsonSerializer.SerializeToNode(DraftOrderInputV4Tests.Empty with { Entries = [DraftOrderInputV4Tests.Line with { Quantity = "10", Price = "20" }, DraftOrderInputV4Tests.Line with { Quantity = "20", Price = "5" }] }, DraftOrderInput.JsonOptions)!;
        node["entries"]![0]!["discount"] = JsonNode.Parse("{\"mode\":\"percentage\",\"value\":\"10\"}");
        node["entries"]![1]!["discount"] = null;
        node["orderDiscount"] = JsonNode.Parse("{\"mode\":\"fixed\",\"value\":\"10\"}");
        node["charges"] = JsonSerializer.SerializeToNode(new[] { Charge("shipping", "15", "supplier"), Charge("salesTax", "21.60", "supplier"), Charge("paymentFee", "3", "thirdParty") }, DraftOrderInput.JsonOptions);
        // WHEN calculated THEN reductions precede charges and the bank fee affects only purchase estimate.
        var draft = node.Deserialize<DraftContentV4>(DraftOrderInput.JsonOptions)!;
        Assert.Empty(DraftOrderInputV4.Validate(draft));
        var result = JsonSerializer.SerializeToNode(DraftOrderInputV4.Calculate(draft), DraftOrderInput.JsonOptions)!;
        Assert.Equal("280.0000", result["merchandiseNet"]!.GetValue<string>());
        Assert.Equal("306.6000", result["supplierEstimate"]!.GetValue<string>());
        Assert.Equal("309.6000", result["purchaseEstimate"]!.GetValue<string>());
    }
    [Theory]
    [InlineData("percentage", "50", "0.0001", "0.0001", "0.0000")]
    [InlineData("percentage", "100", "20", "20.0000", "0.0000")]
    [InlineData("fixed", "0", "20", "0.0000", "20.0000")]
    [InlineData("fixed", "5", "20", "5.0000", "15.0000")]
    public void RoundsDiscountOnceAndPreservesZero(string mode, string value, string price, string reduction, string net)
    {
        // GIVEN a rounded line gross and an explicit discount.
        var draft = DraftOrderInputV4Tests.Empty with { Entries = [DraftOrderInputV4Tests.Line with { PriceMode = "lineTotal", Price = price, Discount = new(mode, value) }] };
        // WHEN calculated THEN midpoint rounds up and subtraction retains four-place precision.
        Assert.Empty(DraftOrderInputV4.Validate(draft));
        var result = DraftOrderInputV4.Calculate(draft);
        Assert.Equal(reduction, result.Lines[0].DiscountAmount);
        Assert.Equal(net, result.PurchaseEstimate);
    }
    [Fact]
    public void UnknownBasesAndThirdPartyAmountsStayIndependent()
    {
        // GIVEN an incomplete line and a fixed discount awaiting its base.
        var line = DraftOrderInputV4Tests.Line with { Price = null, Discount = new("fixed", "300") };
        var charge = new DraftCharge(Guid.NewGuid(), "paymentFee", "Bank fee", null, "thirdParty", "Bank", "estimated", null, null);
        var draft = DraftOrderInputV4Tests.Empty with { Entries = [line], OrderDiscount = new("fixed", "400"), Charges = [charge] };
        // WHEN calculated THEN no discount applies to a partial base.
        Assert.Empty(DraftOrderInputV4.Validate(draft));
        var result = DraftOrderInputV4.Calculate(draft);
        Assert.Null(result.MerchandiseNet); Assert.Null(result.OrderDiscountAmount); Assert.Null(result.PurchaseEstimate);
        Assert.Equal(1, result.IncompleteChargeCount);
        // AND completing merchandise leaves supplier totals independent of the unknown bank fee.
        result = DraftOrderInputV4.Calculate(draft with { Entries = [line with { PriceMode = "lineTotal", Price = "1000" }] });
        Assert.Equal("300.0000", result.SupplierEstimate); Assert.Null(result.PurchaseEstimate);
        result = DraftOrderInputV4.Calculate(draft with { Entries = [] });
        Assert.Null(result.SupplierEstimate);
    }
    [Theory]
    [InlineData("fixed", "251")]
    [InlineData("percentage", "100.0001")]
    [InlineData("percentage", "-1")]
    [InlineData("fixed", "1.00001")]
    [InlineData("other", "1")]
    public void RejectsInvalidDiscounts(string mode, string value)
    {
        // GIVEN an invalid discount on a known base WHEN validated THEN its field is identified.
        var errors = DraftOrderInputV4.Validate(DraftOrderInputV4Tests.Empty with { Entries = [DraftOrderInputV4Tests.Line with { Discount = new(mode, value) }] });
        Assert.Contains(errors.Keys, key => key.StartsWith("draft.entries[0].discount", StringComparison.Ordinal));
    }
    [Fact]
    public void RequiresFreshExplanationForEachConfirmedCorrection()
    {
        // GIVEN a persisted confirmed fee with an earlier explanation.
        var saved = new DraftCharge(Guid.NewGuid(), "shipping", "Freight", "10.0000", "supplier", null, "confirmed", null, "Earlier explanation");
        // WHEN the amount changes THEN existing notes cannot justify another correction.
        Assert.Contains("draft.charges[0].notes", DraftOrderInputV4.ValidateConfirmedCorrections([saved], [saved with { Amount = "11.0000" }]));
        Assert.Empty(DraftOrderInputV4.ValidateConfirmedCorrections([saved], [saved with { Amount = "11.0000", Notes = "Earlier explanation; supplier correction" }]));
        Assert.Contains("draft.charges[0].notes", DraftOrderInputV4.ValidateConfirmedCorrections([saved], [saved with { AmountStatus = "estimated" }]));
        Assert.Contains("draft.charges[0].notes", DraftOrderInputV4.ValidateConfirmedCorrections([saved], [saved with { PayeeKind = "thirdParty", PayeeName = "Carrier" }]));
    }
    [Fact]
    public void RequiredPropertiesProtectReplacementsAndStoredSchemaThreeUpgrades()
    {
        // GIVEN a new request body and a saved schema-three entry lacking discount.
        var node = JsonSerializer.SerializeToNode(DraftOrderInputV4Tests.Empty with { Entries = [DraftOrderInputV4Tests.Line] }, DraftOrderInput.JsonOptions)!;
        node["entries"]![0]!.AsObject().Remove("discount");
        // WHEN bound as a new write THEN its missing adjustment property is rejected.
        Assert.Throws<JsonException>(() => node.Deserialize<DraftContentV4>(DraftOrderInput.JsonOptions));
        // AND the same historical content upgrades safely in memory.
        using var stored = JsonDocument.Parse(node.ToJsonString());
        Assert.Null(DraftOrderInputV4.ReadEntries(stored.RootElement, 3)[0].Discount);
        node["entries"]![0]!["discount"] = null;
        node["charges"] = null;
        Assert.Contains("draft.charges", DraftOrderInputV4.Validate(node.Deserialize<DraftContentV4>(DraftOrderInput.JsonOptions)));
        node.AsObject().Remove("charges");
        Assert.Throws<JsonException>(() => node.Deserialize<DraftContentV4>(DraftOrderInput.JsonOptions));
    }
    [Fact]
    public void CombinedMaximumChargesAndLinesRejectAggregateOverflow()
    {
        // GIVEN individually valid maximum line totals and supplier charges.
        var draft = DraftOrderInputV4Tests.Empty with
        {
            Entries = Enumerable.Range(0, 100).Select(_ => DraftOrderInputV4Tests.Line with { PriceMode = "lineTotal", Price = "9999999999999999999.9999" }).ToArray(),
            Charges = [new(Guid.NewGuid(), "shipping", "Freight", "1", "supplier", null, "estimated", null, null)]
        };
        // WHEN aggregate exceeds 21 integer digits THEN a field error prevents an overflowing total.
        Assert.Contains("draft.charges", DraftOrderInputV4.Validate(draft));
    }
    [Fact]
    public void LegacyReceiptCanonicalOmitsOnlyNewFieldsAndKeepsNormalization()
    {
        // GIVEN an old V4 client retry that had no adjustment fields.
        var draft = DraftOrderInputV4.Normalize(DraftOrderInputV4Tests.Empty with { Entries = [DraftOrderInputV4Tests.Line] });
        var expected = JsonSerializer.SerializeToNode(new { operation = "Create", targetId = (Guid?)null, expectedVersion = (string?)null, draft }, DraftOrderInput.JsonOptions)!;
        expected["draft"]!.AsObject().Remove("orderDiscount"); expected["draft"]!.AsObject().Remove("charges");
        expected["draft"]!["entries"]![0]!.AsObject().Remove("discount");
        // WHEN fingerprinted for receipt resolution THEN the pre-upgrade property order and values survive.
        Assert.Equal(expected.ToJsonString(DraftOrderInput.JsonOptions), DraftOrderInputV4.Canonical("Create", null, null, draft, true));
        Assert.Contains("\"charges\":[]", DraftOrderInputV4.Canonical("Create", null, null, draft));
    }
    [Fact]
    public async Task LegacyRetryMiddlewareAddsBindingDefaultsAndMarksReceiptOnlyCanonical()
    {
        // GIVEN a bounded pre-upgrade V4 write body.
        var node = JsonSerializer.SerializeToNode(new { requestId = Guid.NewGuid(), draft = DraftOrderInputV4Tests.Empty with { Entries = [DraftOrderInputV4Tests.Line] } }, DraftOrderInput.JsonOptions)!;
        node["draft"]!.AsObject().Remove("orderDiscount"); node["draft"]!.AsObject().Remove("charges");
        node["draft"]!["entries"]![0]!.AsObject().Remove("discount");
        var context = new DefaultHttpContext(); context.Request.Path = "/api/v4/purchase-order-drafts"; context.Request.Method = "POST";
        var original = new MemoryStream(Encoding.UTF8.GetBytes(node.ToJsonString())); context.Request.Body = original;
        var invoked = false;
        var middleware = new DraftOrderRequestMiddleware(async request =>
        {
            // WHEN the old body reaches binding THEN required defaults are present and receipt-only mode is marked.
            var parsed = await JsonSerializer.DeserializeAsync<CreateDraftOrderRequestV4>(request.Request.Body, DraftOrderInput.JsonOptions);
            Assert.Empty(parsed!.Draft.Charges); Assert.Null(parsed.Draft.Entries[0].Discount);
            Assert.True(request.Items.ContainsKey(DraftOrderRequestMiddleware.LegacyV4Key)); invoked = true;
        });
        await middleware.InvokeAsync(context);
        Assert.True(invoked); Assert.Same(original, context.Request.Body);
    }
    [Theory]
    [InlineData("{\"draft\":{\"orderDiscount\":null,\"orderDiscount\":null}}", "/calculate")]
    [InlineData("[]", "")]
    public async Task DuplicateOrMalformedAdjustmentBodiesAreRejectedBeforeBinding(string body, string suffix)
    {
        // GIVEN duplicate financial input properties that could otherwise disappear during binding.
        var context = new DefaultHttpContext(); context.Request.Path = "/api/v4/purchase-order-drafts" + suffix; context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        var invoked = false; var middleware = new DraftOrderRequestMiddleware(_ => { invoked = true; return Task.CompletedTask; });
        // WHEN bounded middleware inspects the request THEN ambiguity is rejected before calculation or persistence.
        await middleware.InvokeAsync(context);
        Assert.False(invoked); Assert.Equal(400, context.Response.StatusCode);
    }
    private static object Charge(string category, string amount, string payeeKind) => new { id = Guid.NewGuid(), category, label = category, amount, payeeKind, payeeName = payeeKind == "thirdParty" ? "Bank" : null, amountStatus = "estimated", reference = (string?)null, notes = (string?)null };
}
