// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Gemology;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class GemReferenceCursorTests
{
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
