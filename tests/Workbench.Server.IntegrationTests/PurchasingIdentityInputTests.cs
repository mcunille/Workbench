// Copyright (c) 2026 The White Stag Collection.
using Workbench.Server.Purchasing;
using Xunit;
namespace Workbench.Server.IntegrationTests;

public sealed class PurchasingIdentityInputTests
{
    private static SupplierContent Contact => new(" Supplier ", " Contact ", " a@example.test ", " +1 555 ext 2 ", " https://example.test ", " First\nSecond ");
    private static DraftContentV2 Empty => new(null, null, null, null, [], [], null, null, null, null, null, null, null, null);
    [Fact]
    public void NormalizeTrimsSingleLinesAndPreservesAddressWithoutChangingPurchaseIdentity()
    {
        // GIVEN separately entered supplier details and a transaction platform.
        var id = Guid.NewGuid(); var input = Empty with { SupplierId = id, SupplierName = Contact.Name, SupplierEmail = Contact.Email, SupplierPostalAddress = Contact.PostalAddress, Platform = " Instagram ", SupplierOrderReference = " External " };
        // WHEN normalized THEN single lines are trimmed, addresses remain multiline and optional whitespace becomes absent.
        var normalized = PurchasingIdentityInput.Normalize(input);
        Assert.Equal(id, normalized.SupplierId); Assert.Equal("Supplier", normalized.SupplierName); Assert.Equal("a@example.test", normalized.SupplierEmail);
        Assert.Equal(" First\nSecond ", normalized.SupplierPostalAddress); Assert.Equal("Instagram", normalized.Platform); Assert.Equal("External", normalized.SupplierOrderReference);
        Assert.Null(PurchasingIdentityInput.Normalize(input with { Platform = " \t", SupplierEmail = "\u2000" }).Platform);
        Assert.Empty(PurchasingIdentityInput.Validate(normalized));
    }
    [Theory]
    [InlineData("a\u00a0b@example.test")]
    [InlineData("a\u2000b@example.test")]
    [InlineData("a\u2028b@example.test")]
    [InlineData("a@@example.test")]
    [InlineData("a@example.test,b@example.test")]
    [InlineData("@example.test")]
    public void EmailValidationRejectsMultipleOrWhitespaceAddresses(string email)
    {
        // GIVEN one malformed email address WHEN either boundary validates it THEN the stable field error identifies email.
        Assert.Contains("supplier.email", PurchasingIdentityInput.Validate(PurchasingIdentityInput.Normalize(Contact with { Email = email })));
        Assert.Contains("draft.supplierEmail", PurchasingIdentityInput.Validate(Empty with { SupplierEmail = email }));
    }
    [Fact]
    public void RequiredDirectoryNameAndContactLimitsAreAuthoritative()
    {
        // GIVEN incomplete purchase details and missing or oversized directory/contact input.
        Assert.Empty(PurchasingIdentityInput.Validate(Empty));
        // WHEN validated THEN only the directory requires a name and every bounded contact field rejects overflow.
        Assert.Contains("supplier.name", PurchasingIdentityInput.Validate(Contact with { Name = null! }));
        var invalid = Empty with { SupplierName = new('n', 201), SupplierContactName = new('c', 201), SupplierEmail = new('e', 255), SupplierPhone = new('p', 101), SupplierWebsite = "https://example.test/" + new string('w', 2048), SupplierPostalAddress = new('a', 2001), SupplierOrderReference = new('r', 201), Platform = "Bad\u0001platform", SupplierId = Guid.Empty };
        var errors = PurchasingIdentityInput.Validate(invalid);
        foreach (var field in new[] { "supplierName", "supplierContactName", "supplierEmail", "supplierPhone", "supplierWebsite", "supplierPostalAddress", "supplierOrderReference", "platform", "supplierId" }) Assert.Contains("draft." + field, errors);
    }
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:password@example.test")]
    [InlineData("/relative")]
    [InlineData("https://example.test/white space")]
    public void WebsiteValidationRejectsUnsafeOrNonAbsoluteValues(string website)
    {
        // GIVEN a website which violates the existing HTTP(S) link contract WHEN validated THEN a field error prevents saving.
        Assert.Contains("supplier.website", PurchasingIdentityInput.Validate(Contact with { Website = website }));
    }
    [Fact]
    public void CursorBindingsAndFingerprintsIncludeTransactionDetails()
    {
        // GIVEN a page cursor for one tenant/query and saved draft fingerprint input.
        var timestamp = DateTimeOffset.Parse("2026-09-12T00:00:00+00:00"); var id = Guid.NewGuid(); var cursor = PurchasingIdentityInput.Cursor(timestamp, id, "tenant:QUERY");
        // WHEN decoding a different binding THEN reuse is rejected, while an exact binding preserves ordering.
        Assert.True(PurchasingIdentityInput.Decode(cursor, "tenant:QUERY", out var decoded, out var decodedId)); Assert.Equal(timestamp, decoded); Assert.Equal(id, decodedId);
        Assert.False(PurchasingIdentityInput.Decode(cursor, "tenant:OTHER", out _, out _)); Assert.False(PurchasingIdentityInput.Decode(cursor, "foreign:QUERY", out _, out _));
        Assert.NotEqual(PurchasingIdentityInput.Canonical("Create", null, null, Empty), PurchasingIdentityInput.Canonical("Create", null, null, Empty with { Platform = "Instagram" }));
        Assert.Equal("PO-000001", PurchasingIdentityInput.Reference(1)); Assert.Equal("PO-1000000", PurchasingIdentityInput.Reference(1000000));
    }
}
