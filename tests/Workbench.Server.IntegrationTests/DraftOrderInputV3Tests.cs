// Copyright (c) 2026 The White Stag Collection.
using Workbench.Server.Purchasing;
using Xunit;
namespace Workbench.Server.IntegrationTests;

public sealed class DraftOrderInputV3Tests
{
    internal static DraftContentV3 Empty => new(null, null, "USD", null, [], [], null, null, null, null, null, null, null, null);
    internal static DraftEntryV3 Line => new(Guid.NewGuid(), null, null, null, null, "10", "piece", "20", "piece", "1", null, null, null);
    [Theory]
    [InlineData("10", "20", "1", "200.0000")]
    [InlineData("250", "8", "100", "20.0000")]
    [InlineData("1", "1", "3", "0.3333")]
    [InlineData("1", "0.0001", "2", "0.0001")]
    [InlineData("999999999.9999", "999999999999999.9999", "999999999.9999", "999999999999999.9999")]
    public void CalculatesExactGrossWithoutIntermediateDecimalRounding(string quantity, string price, string per, string expected)
    {
        // GIVEN an explicitly priced line, including values beyond decimal intermediate precision.
        var draft = Empty with { Entries = [Line with { Quantity = quantity, UnitPrice = price, PricePerQuantity = per }] };
        // WHEN calculated THEN exact rational rounding yields the agreed estimate.
        var result = DraftOrderInputV3.Calculate(draft);
        Assert.Equal(expected, result.MerchandiseEstimate);
        Assert.Equal(expected, Assert.Single(result.Lines).Gross);
        Assert.Equal(0, result.IncompleteLineCount);
    }
    [Fact]
    public void SeparatePricingQuantityAndIncompleteZeroRemainDistinct()
    {
        // GIVEN stones priced by weight and an incomplete free line.
        var draft = Empty with { Entries = [Line with { PricingUnit = "carat", PricingQuantity = "12.5" }, Line with { UnitPrice = "0", Quantity = null }] };
        // WHEN calculated THEN the known subtotal excludes the incomplete line.
        var result = DraftOrderInputV3.Calculate(draft);
        Assert.Equal("250.0000", result.MerchandiseEstimate);
        Assert.Equal(1, result.IncompleteLineCount);
        Assert.Null(result.Lines[1].Gross);
        Assert.Null(DraftOrderInputV3.Calculate(Empty).MerchandiseEstimate);
    }
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1e2")]
    [InlineData(" 1")]
    [InlineData("01")]
    [InlineData("1.00001")]
    [InlineData("1000000000")]
    public void RejectsInvalidQuantityAndDenominator(string invalid)
    {
        // GIVEN invalid decimal input WHEN validated THEN each quantity field has its own error.
        var errors = DraftOrderInputV3.Validate(Empty with { Entries = [Line with { Quantity = invalid, PricePerQuantity = invalid, PricingUnit = "carat", PricingQuantity = invalid }] });
        foreach (var field in new[] { "quantity", "pricePerQuantity", "pricingQuantity" }) Assert.Contains("draft.entries[0]." + field, errors);
    }
    [Fact]
    public void ValidatesUnknownUnitsContradictoryBasisCurrencyAndGrossBounds()
    {
        // GIVEN contradictory measures, simultaneous legacy and unit prices, and an excessive line gross.
        var input = Empty with { Currency = null, Entries = [Line with { UnitOfMeasure = "stones", PricingUnit = "stones", PricingQuantity = "1", IndicativePrice = "2" }, Line with { Quantity = "999999999", UnitPrice = "999999999999999", PricePerQuantity = "0.0001" }] };
        // WHEN validated THEN stable field errors prevent persistence.
        var errors = DraftOrderInputV3.Validate(input);
        foreach (var field in new[] { "draft.currency", "draft.entries[0].unitOfMeasure", "draft.entries[0].pricingUnit", "draft.entries[0].pricingQuantity", "draft.entries[0].unitPrice", "draft.entries[1].unitPrice" }) Assert.Contains(field, errors);
    }
    [Fact]
    public void NormalizesWithoutInventingUnknownBasisAndPreservesLegacyMeaning()
    {
        // GIVEN a legacy reference price and an incomplete new entry.
        var legacy = new ReceiptDraftEntryV1(Guid.NewGuid(), " stone ", null, null, "20");
        var input = Empty with { Entries = [DraftOrderInputV3.Upgrade(legacy), Line with { Quantity = "2.5", PricingUnit = null, PricePerQuantity = null, SupplierSku = " SKU ", ItemType = " Gemstone " }] };
        // WHEN normalized THEN precision and text are canonical but the missing basis remains missing.
        var result = DraftOrderInputV3.Normalize(input);
        Assert.Empty(DraftOrderInputV3.Validate(result));
        Assert.Equal("20.0000", result.Entries[0].IndicativePrice);
        Assert.Null(result.Entries[0].UnitPrice);
        Assert.Null(result.Entries[0].Quantity);
        Assert.Equal("2.5000", result.Entries[1].Quantity);
        Assert.Null(result.Entries[1].PricingUnit);
        Assert.Null(result.Entries[1].PricePerQuantity);
        Assert.Equal("SKU", result.Entries[1].SupplierSku);
        Assert.Equal("Gemstone", result.Entries[1].ItemType);
        Assert.Null(DraftOrderInputV3.Legacy(result.Entries[1]).IndicativePrice);
    }
    [Fact]
    public void SumsRoundedLinesAndDistinguishesFreeFromUnknown()
    {
        // GIVEN two individually rounded half units and one explicitly free complete line.
        var draft = Empty with { Entries = [Line with { Quantity = "1", UnitPrice = "0.0001", PricePerQuantity = "2" }, Line with { Quantity = "1", UnitPrice = "0.0001", PricePerQuantity = "2" }, Line with { UnitPrice = "0" }] };
        // WHEN calculated THEN the sum uses rounded lines without dropping the explicit zero.
        var result = DraftOrderInputV3.Calculate(draft);
        Assert.Equal("0.0002", result.MerchandiseEstimate);
        Assert.Equal("0.0000", result.Lines[2].Gross);
        Assert.Equal(0, result.IncompleteLineCount);
    }
    [Fact]
    public void FingerprintIncludesEveryNewFieldAndRejectsUnknownOrMissingProperties()
    {
        // GIVEN canonical V3 input with required nullable fields.
        var draft = Empty with { Entries = [Line] };
        var json = DraftOrderInputV3.Canonical("Create", null, null, draft);
        // WHEN values change THEN request identity changes and the closed entry contract is enforced.
        foreach (var line in new[] { Line with { Quantity = "3" }, Line with { UnitOfMeasure = "carat" }, Line with { UnitPrice = "3" }, Line with { PricingUnit = "gram" }, Line with { PricePerQuantity = "3" }, Line with { PricingQuantity = "3" }, Line with { SupplierSku = "sku" }, Line with { ItemType = "type" } })
            Assert.NotEqual(json, DraftOrderInputV3.Canonical("Create", null, null, draft with { Entries = [line with { Id = draft.Entries[0].Id }] }));
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var entryJson = System.Text.Json.JsonSerializer.Serialize(draft.Entries[0], options);
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<DraftEntryV3>(entryJson.Replace(",\"itemType\":null", ""), options));
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<DraftEntryV3>(entryJson[..^1] + ",\"gross\":\"200.0000\"}", options));
    }
    [Fact]
    public void SummedEstimateSupportsOneHundredMaximumScaleLines()
    {
        // GIVEN one hundred individually valid 19-integer-digit line amounts.
        var draft = Empty with { Entries = Enumerable.Range(0, 100).Select(_ => Line with { Quantity = "999999999", UnitPrice = "9999999999" }).ToArray() };
        // WHEN validated and summed THEN the 21-integer-digit aggregate remains exact.
        Assert.Empty(DraftOrderInputV3.Validate(draft));
        Assert.Equal("999999998900000000100.0000", DraftOrderInputV3.Calculate(draft).MerchandiseEstimate);
    }
    [Fact]
    public void EverySupportedUnitKeepsFractionsAndOuncesRequireExplicitPricingQuantity()
    {
        // GIVEN fractional order amounts across the entire agreed vocabulary.
        foreach (var unit in new[] { "piece", "carat", "gram", "kilogram", "ounce", "troyOunce", "millimeter", "centimeter", "meter", "parcel", "pair", "set", "pack", "box", "lot" })
        {
            var draft = Empty with { Entries = [Line with { Quantity = "0.25", UnitOfMeasure = unit, PricingUnit = unit }] };
            // WHEN validated and calculated THEN fractions remain meaningful regardless of unit label.
            Assert.Empty(DraftOrderInputV3.Validate(draft));
            Assert.Equal("5.0000", DraftOrderInputV3.Calculate(draft).MerchandiseEstimate);
        }
        var different = Empty with { Entries = [Line with { UnitOfMeasure = "ounce", PricingUnit = "troyOunce" }] };
        Assert.Empty(DraftOrderInputV3.Validate(different));
        Assert.Null(DraftOrderInputV3.Calculate(different).MerchandiseEstimate);
    }
}
