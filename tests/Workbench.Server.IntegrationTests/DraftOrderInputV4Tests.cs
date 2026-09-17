// Copyright (c) 2026 The White Stag Collection.
using Workbench.Server.Purchasing;
using Xunit;
namespace Workbench.Server.IntegrationTests;

public sealed class DraftOrderInputV4Tests
{
    internal static DraftContent Empty => new(null, null, "USD", null, [], [], null, null, null, null, null, null, null, null);
    internal static DraftEntry Line => new(Guid.NewGuid(), null, null, null, null, "12.5", "carat", "perUnit", "20", null, null, null);
    [Fact]
    public void CalculatesSupplierBasisAndStandaloneTotalWithUnknownAndZeroDistinct()
    {
        // GIVEN a supplier weight quote, fixed total, unknown price and explicit free line.
        var draft = Empty with { Entries = [Line, Line with { Quantity = null, UnitOfMeasure = null, PriceMode = "lineTotal", Price = "20" }, Line with { Price = null }, Line with { Price = "0" }] };
        // WHEN calculated THEN fixed totals need no quantity and unknown prices remain incomplete.
        var result = DraftOrderInput.Calculate(draft);
        Assert.Equal("270.0000", result.MerchandiseEstimate);
        Assert.Equal(1, result.IncompleteLineCount);
        Assert.Equal("0.0000", result.Lines[3].Gross);
    }
    [Theory]
    [InlineData("250", "8", "100", "perUnit", "0.0800", "20.0000")]
    [InlineData("1", "1", "3", "lineTotal", "0.3333", "0.3333")]
    [InlineData("1", "0.0001", "2", "lineTotal", "0.0001", "0.0001")]
    public void ConversionPreservesEveryRoundedGross(string quantity, string price, string denominator, string mode, string amount, string gross)
    {
        // GIVEN a saved quote whose per-unit rate may require more than four places.
        var original = DraftOrderInputV3Tests.Line with { Quantity = quantity, UnitPrice = price, PricePerQuantity = denominator };
        // WHEN upgraded THEN exact rates remain per-unit and nonrepresentable rates become exact totals.
        var converted = DraftOrderInput.Upgrade(original);
        Assert.Equal(mode, converted.PriceMode); Assert.Equal(amount, converted.Price);
        Assert.Equal(gross, DraftOrderInput.Calculate(Empty with { Entries = [converted] }).MerchandiseEstimate);
    }
    [Fact]
    public void KeepsUnconfirmedReferenceAndIncompleteQuoteWithoutInventingBasis()
    {
        // GIVEN a historical unconfirmed amount and a quote lacking priced weight.
        var reference = DraftOrderInput.Upgrade(DraftOrderInputV3Tests.Line with { UnitPrice = null, IndicativePrice = "20", PricingUnit = null, PricePerQuantity = null });
        var quote = DraftOrderInput.Upgrade(DraftOrderInputV3Tests.Line with { PricingUnit = "carat", PricingQuantity = null });
        // WHEN read THEN both retain their amounts but neither contributes an estimate.
        Assert.Equal("20", reference.IndicativePrice); Assert.Null(reference.Price);
        Assert.Equal("20", quote.LegacyPricing!.UnitPrice); Assert.Null(quote.Price);
        Assert.Null(DraftOrderInput.Calculate(Empty with { Entries = [reference, quote] }).MerchandiseEstimate);
    }
    [Fact]
    public void NormalizesRetainedQuotesForRestrictedPersistence()
    {
        // GIVEN an unresolved supplier quote with noncanonical decimal strings.
        var quote = Line with { Price = null, LegacyPricing = new("10", "piece", "20", "carat", "1", null) };
        // WHEN normalized THEN retained numeric fields use the SQL canonical four-place representation.
        var normalized = DraftOrderInput.Normalize(Empty with { Entries = [quote] });
        Assert.Equal("20.0000", normalized.Entries[0].LegacyPricing!.UnitPrice);
        Assert.Equal("1.0000", normalized.Entries[0].LegacyPricing!.PricePerQuantity);
    }
    [Fact]
    public void RetainedQuoteValidationDoesNotMisattributeUnrelatedMetadataErrors()
    {
        // GIVEN a valid retained quote and an overlong order title.
        var quote = Line with { Price = null, LegacyPricing = new("10", "piece", "20", "carat", "1", null) };
        var draft = Empty with { Title = new string('x', 201), Entries = [quote] };
        // WHEN validated THEN only the editable title is invalid, not the historical quote.
        var errors = DraftOrderInput.Validate(draft);
        Assert.Contains("draft.title", errors);
        Assert.DoesNotContain("draft.entries[0].legacyPricing", errors);
    }
    [Fact]
    public void RetainedQuoteWithoutCurrencyTargetsTheEditableCurrencyField()
    {
        // GIVEN a retained quoted amount with no order currency.
        var quote = Line with { Price = null, LegacyPricing = new("10", "piece", "20", "carat", "1", null) };
        // WHEN validated THEN selecting currency resolves the error without changing the retained quote.
        var errors = DraftOrderInput.Validate(Empty with { Currency = null, Entries = [quote] });
        Assert.Contains("draft.currency", errors);
        Assert.DoesNotContain("draft.entries[0].legacyPricing", errors);
    }
    [Theory]
    [InlineData("-1")]
    [InlineData("1.00001")]
    [InlineData("10000000000000000000")]
    public void RejectsInvalidTotals(string price)
    {
        // GIVEN an invalid total WHEN validated THEN the price field explains the failure.
        Assert.Contains("draft.entries[0].price", DraftOrderInput.Validate(Empty with { Entries = [Line with { PriceMode = "lineTotal", Price = price }] }));
    }
    [Fact]
    public void RequiresCurrencyAndRejectsContradictoryReferencesAndOverflow()
    {
        // GIVEN a known price without currency and an excessive per-unit result.
        Assert.Contains("draft.currency", DraftOrderInput.Validate(Empty with { Currency = null, Entries = [Line] }));
        Assert.Contains("draft.entries[0].price", DraftOrderInput.Validate(Empty with { Entries = [Line with { IndicativePrice = "2" }] }));
        Assert.Contains("draft.entries[0].price", DraftOrderInput.Validate(Empty with { Entries = [Line with { Quantity = "999999999", Price = "999999999999999" }] }));
        // WHEN a maximum supported fixed total is normalized THEN all nineteen integer digits survive.
        var total = DraftOrderInput.Normalize(Empty with { Entries = [Line with { PriceMode = "lineTotal", Price = "9999999999999999999.9999" }] });
        Assert.Empty(DraftOrderInput.Validate(total));
        Assert.Equal("9999999999999999999.9999", DraftOrderInput.Calculate(total).MerchandiseEstimate);
    }
}
