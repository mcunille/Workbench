// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Gemology;
using System.Text;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class GemReferenceCursorTests
{
    [Fact]
    public async Task IncompleteUtf8NameIsRejected()
    {
        await Task.Yield();
        // GIVEN a valid cursor whose long name ends with an incomplete UTF-8 sequence.
        var name = new string('a', 200);
        var search = new GemReferenceSearch(null, null, null);
        var bytes = Convert.FromBase64String(GemReferenceCursor.Encode(new(name, Guid.NewGuid()), search));
        var nameOffset = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(name));
        Assert.True(nameOffset >= 0);
        bytes[nameOffset + name.Length - 1] = 0xc3;
        // WHEN decoding THEN malformed text cannot silently become a different valid position.
        Assert.False(GemReferenceCursor.TryDecode(Convert.ToBase64String(bytes), search, out _));
    }

    [Theory]
    [InlineData('\u7389')]
    [InlineData('\ue000')]
    [InlineData('"')]
    public async Task MaximumLengthUnicodeFiltersCanContinue(char character)
    {
        await Task.Yield();
        // GIVEN valid maximum-length names and filters, including compatibility and escape-heavy text.
        var text = new string(character, 200);
        var position = new GemReferencePosition(text, Guid.NewGuid());
        var search = new GemReferenceSearch(text, "mineral", text);
        // WHEN issuing a continuation THEN the bounded decoder accepts it without changing values.
        var cursor = GemReferenceCursor.Encode(position, search);
        Assert.True(cursor.Length <= 4096);
        Assert.True(GemReferenceCursor.TryDecode(cursor, search, out var decoded));
        Assert.Equal(position, decoded);
    }
}
