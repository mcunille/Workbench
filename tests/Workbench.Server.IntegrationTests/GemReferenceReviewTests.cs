// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Gemology;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class GemReferenceReviewTests
{
    [Fact]
    public Task ReviewIncludesFieldSourceAndRetirementChangesAndStaleStatus()
    {
        // GIVEN published content and a sourced correction/retirement based on an older version.
        var published = GemReferenceSamples.Mineral();
        var changed = published with
        {
            CommonName = "Corrected ruby",
            IsRetired = true,
            RetirementExplanation = "Reclassified",
            Sources = published.Sources.Select(s => s with { Publisher = "Revised publisher" }).ToArray()
        };
        var draft = new GemReferenceDraftResponse(Guid.NewGuid(), changed.Id, changed, "old", "draft", Guid.NewGuid(),
            Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new Dictionary<string, string[]>());
        // WHEN reviewing THEN names, claim sources, retirement, and stale base are explicit.
        var review = GemReferenceReview.Build(draft, published, "new", [changed], new(2026, 10, 2));
        Assert.True(review.IsStale);
        Assert.Contains(review.Changes, c => c.Field == "commonName" && Equals(c.After, changed.CommonName));
        Assert.Contains(review.Changes, c => c.Field == "sources");
        Assert.Contains(review.Changes, c => c.Field == "isRetired");
        Assert.Contains(review.Changes, c => c.Field == "retirementExplanation");
        Assert.Empty(review.Errors);
        return Task.CompletedTask;
    }
}
