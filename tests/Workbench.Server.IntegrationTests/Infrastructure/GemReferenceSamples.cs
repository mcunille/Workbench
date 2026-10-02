// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Gemology;

namespace Workbench.Server.IntegrationTests.Infrastructure;

internal static class GemReferenceSamples
{
    internal static readonly DateOnly ReviewDate = new(2026, 9, 29);

    internal static GemReferenceContent Mineral() => new(
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), "mineral", "Ruby", null,
        "Corundum", "Ruby", null, [],
        [Source("materialKind"), Source("commonName"), Source("species"), Source("variety")],
        null, false, null, null);

    internal static GemReferenceSourceContent Source(string field) => new(Guid.NewGuid(), field,
        "Synthetic classification reference", "Test publisher", "https://example.com/reference",
        null, ReviewDate, ReviewDate);
}
