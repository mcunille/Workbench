// Copyright (c) 2026 The White Stag Collection.
using Workbench.Server.Purchasing;
using Xunit;
namespace Workbench.Server.IntegrationTests;

public sealed class PurchasingIdentityInputTests
{
    private static SupplierContent Contact => new(" Supplier ", " Contact ", " a@example.test ", " +1 555 ext 2 ", " https://example.test ", " First\nSecond ");
    [Fact]
    public void ProfilesAcceptUserDefinedPlainHandles()
    {
        // GIVEN arbitrary platform labels and plain reference handles, including URL-like text.
        const string json = """{"name":"Supplier","contactName":null,"email":null,"phone":null,"website":null,"postalAddress":null,"socialProfiles":[{"label":"Discord","handle":"@someone (primary)"},{"label":"Other","handle":"javascript:reference"}]}""";
        // WHEN decoded and validated THEN the open set of labels and non-link handles is accepted.
        var content = System.Text.Json.JsonSerializer.Deserialize<SupplierContent>(json, DraftOrderInput.JsonOptions)!;
        Assert.Empty(PurchasingIdentityInput.Validate(content));
    }
    [Fact]
    public void ProfilesNormalizeTrimmedValuesAndPreserveLegacySerialization()
    {
        // GIVEN reference text with outer whitespace WHEN normalized THEN labels and handles are trimmed.
        var normalized = PurchasingIdentityInput.Normalize(Contact with { SocialProfiles = [new(" Discord ", " @someone ")] });
        Assert.Equal(new SupplierSocialProfile("Discord", "@someone"), Assert.Single(normalized.SocialProfiles!));
        Assert.Empty(PurchasingIdentityInput.Validate(normalized));
        // AND omitted, null, and empty entries preserve the legacy six-field canonical bytes.
        var legacy = new { normalized.Name, normalized.ContactName, normalized.Email, normalized.Phone, normalized.Website, normalized.PostalAddress };
        foreach (var profiles in new IReadOnlyList<SupplierSocialProfile>?[] { null, [] })
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(legacy, DraftOrderInput.JsonOptions),
                System.Text.Json.JsonSerializer.Serialize(PurchasingIdentityInput.Normalize(Contact with { SocialProfiles = profiles }), DraftOrderInput.JsonOptions));
    }
    [Theory]
    [InlineData("", "handle", "label")]
    [InlineData(" \t", "handle", "label")]
    [InlineData("Platform", "", "handle")]
    [InlineData("Platform", " \t", "handle")]
    [InlineData("line\nbreak", "handle", "label")]
    [InlineData("Platform", "line\nbreak", "handle")]
    public void ProfilesRejectMissingOrControlText(string label, string handle, string field)
    {
        // GIVEN an incomplete or multiline reference WHEN validated THEN its indexed field has feedback.
        var content = PurchasingIdentityInput.Normalize(Contact with { SocialProfiles = [new(label, handle)] });
        Assert.Contains("supplier.socialProfiles[0]." + field, PurchasingIdentityInput.Validate(content));
    }
    [Fact]
    public void ProfilesEnforceLimitsAndCaseInsensitiveDistinctLabels()
    {
        // GIVEN maximum-sized valid reference text WHEN validated THEN all limits are inclusive.
        var content = PurchasingIdentityInput.Normalize(Contact) with { SocialProfiles = [new(new string('a', 100), new string('b', 2048))] };
        Assert.Empty(PurchasingIdentityInput.Validate(content));
        // AND oversized labels, handles, and lists, duplicates and null elements are rejected.
        Assert.Contains("supplier.socialProfiles[0].label", PurchasingIdentityInput.Validate(content with { SocialProfiles = [new(new string('a', 101), "handle")] }));
        Assert.Contains("supplier.socialProfiles[0].handle", PurchasingIdentityInput.Validate(content with { SocialProfiles = [new("label", new string('b', 2049))] }));
        Assert.Contains("supplier.socialProfiles[1].label", PurchasingIdentityInput.Validate(content with { SocialProfiles = [new("Discord", "one"), new("discord", "two")] }));
        Assert.Contains("supplier.socialProfiles[0].label", PurchasingIdentityInput.Validate(PurchasingIdentityInput.Normalize(content with { SocialProfiles = [null!] })));
        Assert.Empty(PurchasingIdentityInput.Validate(content with { SocialProfiles = Enumerable.Range(0, 20).Select(i => new SupplierSocialProfile($"Label{i}", "handle")).ToArray() }));
        Assert.Contains("supplier.socialProfiles", PurchasingIdentityInput.Validate(content with { SocialProfiles = Enumerable.Range(0, 21).Select(i => new SupplierSocialProfile($"Label{i}", "handle")).ToArray() }));
    }
    private static DraftContent Empty => new(null, null, null, null, [], [], null, null, null, null, null, null, null, null);
    [Fact]
    public void NormalizeTrimsSingleLinesAndPreservesAddressWithoutChangingPurchaseIdentity()
    {
        // GIVEN separately entered supplier details and a transaction platform.
        var id = Guid.NewGuid(); var input = Empty with { SupplierId = id, SupplierName = Contact.Name, SupplierEmail = Contact.Email, SupplierPostalAddress = Contact.PostalAddress, Platform = " Instagram ", SupplierOrderReference = " External " };
        // WHEN normalized THEN single lines are trimmed, addresses remain multiline and optional whitespace becomes absent.
        var normalized = DraftOrderInput.Normalize(input);
        Assert.Equal(id, normalized.SupplierId); Assert.Equal("Supplier", normalized.SupplierName); Assert.Equal("a@example.test", normalized.SupplierEmail);
        Assert.Equal(" First\nSecond ", normalized.SupplierPostalAddress); Assert.Equal("Instagram", normalized.Platform); Assert.Equal("External", normalized.SupplierOrderReference);
        Assert.Null(DraftOrderInput.Normalize(input with { Platform = " \t", SupplierEmail = "\u2000" }).Platform);
        Assert.Empty(DraftOrderInput.Validate(normalized));
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
        Assert.Contains("draft.supplierEmail", DraftOrderInput.Validate(Empty with { SupplierEmail = email }));
    }
    [Fact]
    public void RequiredDirectoryNameAndContactLimitsAreAuthoritative()
    {
        // GIVEN incomplete purchase details and missing or oversized directory/contact input.
        Assert.Empty(DraftOrderInput.Validate(Empty));
        // WHEN validated THEN only the directory requires a name and every bounded contact field rejects overflow.
        Assert.Contains("supplier.name", PurchasingIdentityInput.Validate(Contact with { Name = null! }));
        var invalid = Empty with { SupplierName = new('n', 201), SupplierContactName = new('c', 201), SupplierEmail = new('e', 255), SupplierPhone = new('p', 101), SupplierWebsite = "https://example.test/" + new string('w', 2048), SupplierPostalAddress = new('a', 2001), SupplierOrderReference = new('r', 201), Platform = "Bad\u0001platform", SupplierId = Guid.Empty };
        var errors = DraftOrderInput.Validate(invalid);
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
        Assert.NotEqual(DraftOrderInput.Canonical("Create", null, null, Empty), DraftOrderInput.Canonical("Create", null, null, Empty with { Platform = "Instagram" }));
        Assert.Equal("PO-000001", PurchasingIdentityInput.Reference(1)); Assert.Equal("PO-1000000", PurchasingIdentityInput.Reference(1000000));
    }
}
