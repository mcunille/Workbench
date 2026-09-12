// Copyright (c) 2026 The White Stag Collection.

using System.Text.Json;
using Workbench.Server.Purchasing;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class DraftOrderInputTests
{
    private static DraftContent Empty => new(null, null, null, null, [], []);
    private static DraftEntry Entry(string? price = null) => new(Guid.NewGuid(), null, null, null, price);

    [Fact]
    public void EmptyAndNormalizedOptionalFactsPreserveUnknownAndExplicitZero()
    {
        // GIVEN optional shopping-list facts, padded labels and meaningful notes.
        var input = Empty with
        {
            Title = "  Plan  ",
            SupplierName = "\tSupplier ",
            Currency = "usd",
            Notes = " notes\n ",
            SourceLinks = [" https://example.com/cart "],
            Entries = [Entry() with { Description = " Stone ", Notes = "  \n" }, Entry("0")]
        };
        // WHEN normalizing and validating the reference amounts.
        var normalized = DraftOrderInput.Normalize(input);
        // THEN blank optional fields remain unknown and zero retains an exact decimal meaning.
        Assert.Empty(DraftOrderInput.Validate(Empty));
        Assert.Empty(DraftOrderInput.Validate(normalized));
        Assert.Equal("Plan", normalized.Title);
        Assert.Equal("Supplier", normalized.SupplierName);
        Assert.Equal("USD", normalized.Currency);
        Assert.Null(DraftOrderInput.Normalize(Empty with { Currency = " \t" }).Currency);
        Assert.Equal(input.Notes, normalized.Notes);
        Assert.Equal("https://example.com/cart", normalized.SourceLinks[0]);
        Assert.Equal("Stone", normalized.Entries[0].Description);
        Assert.Null(normalized.Entries[0].Notes);
        Assert.Null(normalized.Entries[0].IndicativePrice);
        Assert.Equal("0.0000", normalized.Entries[1].IndicativePrice);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("01")]
    [InlineData("1e2")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1,000")]
    [InlineData("1.")]
    [InlineData(".1")]
    [InlineData("1.00001")]
    [InlineData("1000000000000000")]
    [InlineData("")]
    public void NonExactPriceSyntaxIsRejected(string price)
    {
        // GIVEN an amount that would require interpretation, rounding or overflow.
        var input = Empty with { Currency = "USD", Entries = [Entry(price)] };
        // WHEN validating normalized input THEN the price field identifies the error.
        Assert.Contains("draft.entries[0].indicativePrice", DraftOrderInput.Validate(DraftOrderInput.Normalize(input)).Keys);
    }

    [Fact]
    public void CurrencyAndCollectionBoundsAreAuthoritative()
    {
        // GIVEN bounded entry/link lists and the largest decimal(19,4) amount.
        Assert.Empty(DraftOrderInput.Validate(Empty with { Currency = "USD", Entries = [Entry("999999999999999.9999")] }));
        // WHEN required price notation, identifiers and collection bounds are violated THEN field errors explain the limit.
        Assert.Contains("draft.currency", DraftOrderInput.Validate(Empty with { Entries = [Entry("0")] }).Keys);
        foreach (var currency in new[] { "US", "USDD", "ÜSD", "U1D", " USD " })
            Assert.Contains("draft.currency", DraftOrderInput.Validate(Empty with { Currency = currency }).Keys);
        var entry = Entry();
        Assert.Contains("draft.entries[1].id", DraftOrderInput.Validate(Empty with { Entries = [entry, entry] }).Keys);
        Assert.Contains("draft.entries[0].id", DraftOrderInput.Validate(Empty with { Entries = [entry with { Id = Guid.Empty }] }).Keys);
        Assert.Contains("draft.entries", DraftOrderInput.Validate(Empty with { Entries = Enumerable.Range(0, 101).Select(_ => Entry()).ToArray() }).Keys);
        Assert.Contains("draft.sourceLinks", DraftOrderInput.Validate(Empty with { SourceLinks = Enumerable.Repeat("https://example.com", 21).ToArray() }).Keys);
        Assert.Empty(DraftOrderInput.Validate(Empty with { SourceLinks = Enumerable.Repeat("https://example.com", 20).ToArray() }));
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/cart?q=1")]
    public void SafeAbsoluteLinksAreAcceptedWithoutFetchingOrRewriting(string link)
    {
        // GIVEN an absolute link without credentials WHEN validating THEN its exact trimmed representation remains.
        var input = DraftOrderInput.Normalize(Empty with { SourceLinks = [link] });
        Assert.Empty(DraftOrderInput.Validate(input));
        Assert.Equal(link, input.SourceLinks[0]);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("/relative")]
    [InlineData("https://user:pass@example.com")]
    [InlineData("https://")]
    [InlineData("file:///tmp/x")]
    [InlineData("")]
    [InlineData("https://example.com/a b")]
    [InlineData("https://example.com/a\tb")]
    public void UnsafeOrIncompleteLinksHaveFieldErrors(string link)
    {
        // GIVEN an unsafe or incomplete source WHEN validating THEN both link locations identify the issue.
        var errors = DraftOrderInput.Validate(Empty with { SourceLinks = [link], Entries = [Entry() with { SourceLink = link }] });
        Assert.Contains("draft.sourceLinks[0]", errors.Keys);
        Assert.Contains("draft.entries[0].sourceLink", errors.Keys);
    }

    [Fact]
    public void TextLimitsCountUtf16UnitsAndEscapedStorageBytes()
    {
        // GIVEN individually valid maximum fields and escaping-heavy shopping-list notes.
        var input = Empty with
        {
            Title = new string('t', 200),
            SupplierName = new string('s', 200),
            Notes = new string('n', 10000),
            Entries = Enumerable.Range(0, 100).Select(_ => Entry() with { Description = new string('d', 500), Notes = new string('n', 2000) }).ToArray()
        };
        Assert.Empty(DraftOrderInput.Validate(input));
        Assert.Empty(DraftOrderInput.Validate(Empty with { Title = string.Concat(Enumerable.Repeat("\U0001F48E", 100)) }));
        // WHEN scalar or aggregate storage bounds are exceeded THEN no database write is necessary to reject them.
        Assert.Contains("draft.title", DraftOrderInput.Validate(input with { Title = new string('t', 201) }).Keys);
        Assert.Contains("draft.title", DraftOrderInput.Validate(Empty with { Title = string.Concat(Enumerable.Repeat("\U0001F48E", 101)) }).Keys);
        Assert.Contains("draft.supplierName", DraftOrderInput.Validate(input with { SupplierName = new string('s', 201) }).Keys);
        Assert.Contains("draft.notes", DraftOrderInput.Validate(input with { Notes = new string('n', 10001) }).Keys);
        Assert.Contains("draft", DraftOrderInput.Validate(input with { Entries = input.Entries.Select(entry => entry with { Notes = new string('<', 2000) }).ToArray() }).Keys);
        Assert.Contains("draft.entries[0].description", DraftOrderInput.Validate(Empty with { Entries = [Entry() with { Description = new string('d', 501) }] }).Keys);
        Assert.Contains("draft.entries[0].notes", DraftOrderInput.Validate(Empty with { Entries = [Entry() with { Notes = new string('n', 2001) }] }).Keys);
        var longestLink = "https://example.com/" + new string('a', 2028);
        Assert.Equal(2048, longestLink.Length);
        Assert.Empty(DraftOrderInput.Validate(Empty with { SourceLinks = [longestLink] }));
        Assert.Contains("draft.sourceLinks[0]", DraftOrderInput.Validate(Empty with { SourceLinks = [longestLink + "x"] }).Keys);
    }

    [Fact]
    public void CanonicalInputHasStableExplicitFieldOrderAndSemanticIdentity()
    {
        // GIVEN equivalent normalized content and a fixed entry identity.
        var entry = Entry("1.2");
        var first = Empty with { Title = "  Plan ", Currency = "usd", Entries = [entry] };
        var second = Empty with { Title = "Plan", Currency = "USD", Entries = [entry with { IndicativePrice = "1.2000" }] };
        // WHEN preparing fingerprint V1 THEN normalized input is identical but significant changes are distinct.
        var canonical = DraftOrderInput.Canonical("Create", null, null, DraftOrderInput.Normalize(first));
        Assert.Equal(canonical, DraftOrderInput.Canonical("Create", null, null, DraftOrderInput.Normalize(second)));
        Assert.StartsWith("{\"operation\":\"Create\",\"targetId\":null,\"expectedVersion\":null,\"draft\":{\"title\":", canonical);
        Assert.Contains("\"id\":\"" + entry.Id.ToString("D") + "\",\"description\":null,\"notes\":null,\"sourceLink\":null,\"indicativePrice\":\"1.2000\"", canonical);
        Assert.NotEqual(canonical, DraftOrderInput.Canonical("Create", null, null, DraftOrderInput.Normalize(second with { Notes = " " + "note" })));
        var ordered = second with { SourceLinks = ["https://one.example", "https://two.example"] };
        Assert.NotEqual(DraftOrderInput.Canonical("Create", null, null, ordered), DraftOrderInput.Canonical("Create", null, null, ordered with { SourceLinks = ordered.SourceLinks.Reverse().ToArray() }));
        Assert.Contains("\\u003C", DraftOrderInput.Canonical("Create", null, null, Empty with { Notes = "<" }));
    }

    [Fact]
    public void SavedVersionRequiresExactlyEightBase64Bytes()
    {
        // GIVEN the opaque database version WHEN decoding THEN all eight bytes round-trip unchanged.
        var expected = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var errors = new Dictionary<string, string[]>();
        Assert.Equal(expected, DraftOrderInput.NormalizeVersion(expected, errors));
        Assert.Empty(errors);
        // AND missing, truncated, extended, malformed or whitespace-padded versions are field errors.
        foreach (var invalid in new[] { null, "", "AQ==", "not base64", expected + " ", Convert.ToBase64String(new byte[9]) })
        {
            errors.Clear();
            Assert.Null(DraftOrderInput.NormalizeVersion(invalid, errors));
            Assert.Contains("expectedVersion", errors.Keys);
        }
    }

    [Fact]
    public void CursorRoundTripPreservesPrecisionAndRejectsMalformedInput()
    {
        // GIVEN a timestamp with all seven fractional digits.
        var timestamp = DateTimeOffset.Parse("2026-09-12T02:00:00.1234567+00:00");
        var id = Guid.NewGuid();
        // WHEN encoding and decoding THEN the exact key survives.
        Assert.True(DraftOrderCursor.TryDecode(DraftOrderCursor.Encode(timestamp, id), out var decoded, out var decodedId));
        Assert.Equal(timestamp, decoded);
        Assert.Equal(id, decodedId);
        foreach (var invalid in new[] { "", new string('a', 129), $"v2_{timestamp:O}_{id:N}", $"v1_2026-09-12T02:00:00.1234567+01:00_{id:N}",
            $"v1_2026-09-12T02:00:00+00:00_{id:N}", $"v1_{timestamp:O}_{Guid.Empty:N}", $"v1_{timestamp:O}_{id:D}" })
            Assert.False(DraftOrderCursor.TryDecode(invalid, out _, out _));
    }

    [Fact]
    public void WireContractRequiresExplicitNullsAndRejectsNestedUnknownFields()
    {
        // GIVEN incomplete and extended representations of the versioned replacement contract.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        // WHEN binding THEN omitted nullable fields and nested authority fields fail before normalization.
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DraftContent>("{\"sourceLinks\":[],\"entries\":[]}", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DraftContent>("{\"title\":null,\"supplierName\":null,\"currency\":null,\"notes\":null,\"sourceLinks\":[],\"entries\":[],\"tenantId\":null}", options));
    }
}
