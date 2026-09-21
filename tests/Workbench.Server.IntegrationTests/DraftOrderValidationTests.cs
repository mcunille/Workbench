// Copyright (c) 2026 The White Stag Collection.
using Workbench.Server.Purchasing;
using Xunit;
namespace Workbench.Server.IntegrationTests;

public sealed class DraftOrderValidationTests
{
    private static DraftContent Empty => DraftOrderPricingTests.Empty;
    private static DraftEntry Entry => DraftOrderPricingTests.Line with { Price = null };

    [Fact]
    public void MissingCollectionsAndNullEntriesReportStableErrors()
    {
        // GIVEN absent collections and null elements at the public validation boundary.
        var missing = Empty with { Entries = null!, SourceLinks = null! };
        // WHEN validating THEN collection errors replace enumeration failures.
        var errors = DraftOrderInput.Validate(missing);
        Assert.Equal(new[] { "draft.sourceLinks", "draft.entries" }, errors.Keys);
        Assert.Equal("Supply an array of up to 20 source links.", Assert.Single(errors["draft.sourceLinks"]));
        Assert.Equal("Supply an array of up to 100 shopping-list entries.", Assert.Single(errors["draft.entries"]));
        // AND null elements retain their indexed error keys.
        errors = DraftOrderInput.Validate(Empty with { Entries = [null!], SourceLinks = [null!] });
        Assert.Equal(new[] { "draft.sourceLinks[0]", "draft.entries[0]" }, errors.Keys);
        Assert.Equal("An entry object is required.", Assert.Single(errors["draft.entries[0]"]));
        Assert.Equal("Supply a draft.", Assert.Single(DraftOrderInput.Validate(null)["draft"]));
    }

    [Fact]
    public void OversizedCollectionsValidateOnlyTheirSupportedPrefix()
    {
        // GIVEN invalid elements immediately beyond each supported collection bound.
        var entries = Enumerable.Range(0, 100).Select(_ => Entry).Append(Entry with { Id = Guid.Empty, Quantity = "bad", SourceLink = "bad" }).ToArray();
        var links = Enumerable.Repeat("https://example.test", 20).Append("bad").ToArray();
        // WHEN validating THEN the collection limits are the only errors.
        var errors = DraftOrderInput.Validate(Empty with { Entries = entries, SourceLinks = links });
        Assert.Equal(new[] { "draft.sourceLinks", "draft.entries" }, errors.Keys);
    }

    [Fact]
    public void LastSupportedEntryStillReceivesBasicAndQuantityValidation()
    {
        // GIVEN a full list whose final supported entry has invalid basic and quantity fields.
        var entries = Enumerable.Range(0, 99).Select(_ => Entry).Append(Entry with { Description = new('d', 501), Quantity = "0" }).ToArray();
        // WHEN validating THEN both validation stages include entry 100.
        var errors = DraftOrderInput.Validate(Empty with { Entries = entries });
        Assert.Equal(new[] { "draft.entries[99].description", "draft.entries[99].quantity" }, errors.Keys);
    }

    [Fact]
    public void CommonValidationPreservesErrorOrderAndSupplierMessagePrecedence()
    {
        // GIVEN errors from basic fields, supplier identity, transaction metadata and entry details.
        var draft = Empty with
        {
            Title = new('t', 201),
            SupplierName = new('s', 201),
            SupplierId = Guid.Empty,
            SupplierEmail = "bad",
            Platform = "bad\u0001",
            SupplierOrderReference = new('r', 201),
            Entries = [Entry with { Quantity = "0", UnitOfMeasure = "unknown", SupplierSku = new('s', 201), ItemType = new('i', 101) }]
        };
        // WHEN validating THEN supplier messages overwrite basic messages without moving their keys.
        var errors = DraftOrderInput.Validate(draft);
        Assert.Equal(new[] { "draft.title", "draft.supplierName", "draft.supplierEmail", "draft.supplierId", "draft.platform", "draft.supplierOrderReference", "draft.entries[0].quantity", "draft.entries[0].unitOfMeasure", "draft.entries[0].supplierSku", "draft.entries[0].itemType" }, errors.Keys);
        Assert.Equal("Use at most 200 characters without control characters.", Assert.Single(errors["draft.supplierName"]));
        Assert.Equal("Use a positive decimal with up to nine integer digits and four decimal places.", Assert.Single(errors["draft.entries[0].quantity"]));
        Assert.Equal("Choose a supported unit.", Assert.Single(errors["draft.entries[0].unitOfMeasure"]));
        Assert.Equal("Use at most 200 characters.", Assert.Single(errors["draft.entries[0].supplierSku"]));
        Assert.Equal("Use at most 100 characters.", Assert.Single(errors["draft.entries[0].itemType"]));
    }

    [Theory]
    [InlineData("piece")]
    [InlineData("carat")]
    [InlineData("gram")]
    [InlineData("kilogram")]
    [InlineData("ounce")]
    [InlineData("troyOunce")]
    [InlineData("millimeter")]
    [InlineData("centimeter")]
    [InlineData("meter")]
    [InlineData("parcel")]
    [InlineData("pair")]
    [InlineData("set")]
    [InlineData("pack")]
    [InlineData("box")]
    [InlineData("lot")]
    [InlineData(null)]
    public void SupportedUnitsAndMetadataBoundariesAreAccepted(string? unit)
    {
        // GIVEN valid boundary metadata and a supported or absent unit.
        var draft = Empty with { Platform = new('p', 200), SupplierOrderReference = new('r', 200), Entries = [Entry with { Quantity = "0.0001", UnitOfMeasure = unit, SupplierSku = new('s', 200), ItemType = new('i', 100) }] };
        // WHEN validating THEN no field needs correction.
        Assert.Empty(DraftOrderInput.Validate(draft));
    }

    public static TheoryData<string?, string?, string?, string?, string?, string?, string?, bool> RetainedQuotes => new()
    {
        { null, null, null, null, null, null, null, true },
        { null, "piece", "1", "piece", "1", null, null, true },
        { "1", null, "1", "piece", "1", null, null, true },
        { "1", "piece", null, "piece", "1", null, null, true },
        { "1", "piece", "1", null, "1", null, null, true },
        { "1", "piece", "1", "piece", null, null, null, true },
        { "1", "piece", "1", "carat", "1", null, null, true },
        { "1", "piece", "1", "carat", "1", "2", null, true },
        { "1", "piece", "0.0001", "piece", "2", null, null, true },
        { "1", "piece", "0.0001", "piece", "3", null, null, true },
        { "1", "unknown", null, null, null, null, null, false },
        { "1", "piece", null, "unknown", null, null, null, false },
        { "1", "piece", "bad", "piece", "1", null, null, false },
        { "1", "piece", "1", "piece", "1", null, "2", false },
        { "1", null, null, "carat", "1", "1", null, false },
        { "1", "piece", null, null, "1", "1", null, false },
        { "1", "piece", "1", "piece", "1", "1", null, false },
        { "1", "piece", "1", "carat", "0", "1", null, false },
        { "1", "piece", "1", "carat", "1", "0", null, false },
        { "999999999", "piece", "999999999999999", "piece", "0.0001", null, null, false },
        { "10", "piece", "100000000000000", "piece", "0.0001", null, null, false },
        { "1", "piece", "999999999999999", "piece", "0.0001", null, null, true }
    };

    [Theory]
    [MemberData(nameof(RetainedQuotes))]
    public void RetainedQuoteValidationPreservesIncompleteQuotesAndRejectsContradictions(string? quantity, string? unit, string? price, string? pricingUnit, string? pricePerQuantity, string? pricingQuantity, string? reference, bool valid)
    {
        // GIVEN a historical quote with optional basis, units, price and conversion quantity.
        var quote = new DraftLegacyPricing(quantity, unit, price, pricingUnit, pricePerQuantity, pricingQuantity);
        var draft = Empty with { Entries = [Entry with { LegacyPricing = quote, IndicativePrice = reference }] };
        // WHEN validating THEN only contradictory, malformed or overflowing retained quotes are rejected.
        var errors = DraftOrderInput.Validate(draft);
        if (valid) Assert.Empty(errors);
        else Assert.Equal("The retained quote is invalid.", Assert.Single(Assert.Single(errors).Value));
    }

    [Theory]
    [InlineData("!!!!!!!!!!!!")]
    [InlineData("AQIDBAUGBw==")]
    public void SavedVersionRejectsMalformedOrShortPayloadsAtTheExpectedTextLength(string version)
    {
        // GIVEN twelve characters that do not encode exactly eight bytes.
        Dictionary<string, string[]> errors = [];
        // WHEN normalizing THEN the version is rejected with the stable field message.
        Assert.Null(DraftOrderInput.NormalizeVersion(version, errors));
        Assert.Equal("A valid saved version is required.", Assert.Single(errors["expectedVersion"]));
    }
}
