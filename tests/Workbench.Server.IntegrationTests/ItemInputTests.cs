// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Inventory;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class ItemInputTests
{
    [Fact]
    public void NormalizationPreservesNonblankNotesAndTrimsNamesAndLocations()
    {
        // GIVEN outer whitespace and notes whose formatting matters.
        var request = new CreateItemRequest(Guid.NewGuid(), "\u2003Sapphire\t", "  note\r\nline  ", "\tTray A\u00a0");
        // WHEN normalizing the submitted fields.
        var normalized = ItemInput.Normalize(request);
        // THEN only name and location are trimmed and meaningful notes remain exact.
        Assert.Equal("Sapphire", normalized.Name);
        Assert.Equal("Tray A", normalized.Location);
        Assert.Equal(request.Notes, normalized.Notes);
        Assert.Empty(ItemInput.Validate(normalized));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\n\u2003\u00a0")]
    public void BlankOptionalFieldsBecomeNullAndBlankNamesAreRejected(string? value)
    {
        // GIVEN absent or whitespace-only inputs WHEN normalized and validated.
        var normalized = ItemInput.Normalize(new CreateItemRequest(Guid.NewGuid(), value, value, value));
        // THEN optional fields become null and the required name is invalid.
        Assert.Null(normalized.Notes);
        Assert.Null(normalized.Location);
        Assert.Contains("name", ItemInput.Validate(normalized).Keys);
    }

    [Theory]
    [InlineData(200, 4000, 200, true)]
    [InlineData(201, 4000, 200, false)]
    [InlineData(200, 4001, 200, false)]
    [InlineData(200, 4000, 201, false)]
    public void LimitsUseUtf16CodeUnits(int nameLength, int notesLength, int locationLength, bool valid)
    {
        // GIVEN fields on or beyond each independent boundary.
        var request = new CreateItemRequest(Guid.NewGuid(), new string('n', nameLength), new string('a', notesLength), new string('l', locationLength));
        // WHEN validating THEN exact limits pass without truncating excess values.
        Assert.Equal(valid, ItemInput.Validate(ItemInput.Normalize(request)).Count == 0);
    }

    [Fact]
    public void EmptyCreationRequestIdentifierIsRejected()
    {
        // GIVEN valid fields but no operation identity WHEN validating THEN retry safety is required.
        Assert.Contains("creationRequestId", ItemInput.Validate(new CreateItemRequest(Guid.Empty, "Ring", null, null)).Keys);
    }
}
