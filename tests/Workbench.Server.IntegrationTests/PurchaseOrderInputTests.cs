// Copyright (c) 2026 The White Stag Collection.
using Workbench.Server.Purchasing;
using Xunit;
namespace Workbench.Server.IntegrationTests;

public sealed class PurchaseOrderInputTests
{
    private static DraftContent Valid => DraftOrderInput.Normalize(DraftOrderPricingTests.Empty with { SupplierName = "Supplier", Entries = [DraftOrderPricingTests.Line with { Description = "Stone", Price = null }] });
    [Fact]
    public void UnknownPricesRemainValidButEveryOrderedLineNeedsAnIdentityAndQuantity()
    {
        // GIVEN incomplete prices with otherwise complete ordered lines.
        var valid = Valid;
        // WHEN validating commitment THEN unknown costs remain permissible.
        Assert.Empty(PurchaseOrderInput.Validate(valid, "2026-09-11", null, false));
        // AND every required identity and quantity field produces actionable errors, including total-line pricing.
        foreach (var (draft, key) in new (DraftContent, string)[] {
            (valid with { SupplierName = null }, "draft.supplierName"), (valid with { Currency = null }, "draft.currency"),
            (valid with { Entries = [] }, "draft.entries"),
            (valid with { Entries = [valid.Entries[0] with { Description = null }] }, "draft.entries[0].description"),
            (valid with { Entries = [valid.Entries[0] with { Quantity = null, PriceMode = "lineTotal" }] }, "draft.entries[0].quantity"),
            (valid with { Entries = [valid.Entries[0] with { Quantity = "0.0000" }] }, "draft.entries[0].quantity"),
            (valid with { Entries = [valid.Entries[0] with { UnitOfMeasure = null }] }, "draft.entries[0].unitOfMeasure"),
            (valid with { Entries = [valid.Entries[0] with { IndicativePrice = "1.0000" }] }, "draft.entries[0].legacyPricing"),
            (valid with { Entries = [valid.Entries[0] with { LegacyPricing = new(null,null,null,null,null,null) }] }, "draft.entries[0].legacyPricing") })
            Assert.Contains(key, PurchaseOrderInput.Validate(draft, "2026-09-11", null, false));
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-02-30")]
    [InlineData("2026-9-11")]
    [InlineData("2026-09-11T00:00:00Z")]
    public void CommitmentRequiresAnExplicitCalendarDate(string? date)
    {
        // GIVEN missing or malformed calendar input WHEN validating THEN no timestamp inference occurs.
        Assert.Contains("orderDate", PurchaseOrderInput.Validate(Valid, date, null, false));
    }
    [Fact]
    public void AmendmentReasonsUseTrimmedUtf16LengthAndFutureDatesArePermitted()
    {
        // GIVEN a replacement purchase WHEN validating a reason THEN trim boundaries are explicit and future dates work.
        Assert.Empty(PurchaseOrderInput.Validate(Valid, "2999-12-31", "  " + new string('x', 2000) + "  ", true));
        foreach (var reason in new[] { null, "", " \t\u2000", new string('x', 2001) })
            Assert.Contains("reason", PurchaseOrderInput.Validate(Valid, "2999-12-31", reason, true));
    }
}
