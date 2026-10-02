// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Gemology;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class GemReferenceInputTests
{
    private static Dictionary<string, string[]> Errors(GemReferenceContent content,
        IReadOnlyDictionary<Guid, Guid?>? redirects = null) =>
        GemReferenceInput.Validate(content, new DateOnly(2026, 10, 1), redirects ?? new Dictionary<Guid, Guid?>());

    [Fact]
    public async Task MineralRequiresSpeciesWhileNonMineralCanOmitTaxonomy()
    {
        await Task.Yield();
        // GIVEN a sourced mineral with missing species.
        var mineral = GemReferenceSamples.Mineral() with { Species = null };
        // WHEN validated THEN species is required.
        Assert.Contains("species", Errors(mineral).Keys);
        // AND organic material needs no invented taxonomy.
        var pearl = mineral with
        {
            MaterialKind = "organic",
            CommonName = "Pearl",
            Variety = null,
            Sources = [GemReferenceSamples.Source("materialKind"), GemReferenceSamples.Source("commonName")]
        };
        Assert.Empty(Errors(pearl));
    }

    [Fact]
    public async Task SourcesFollowThePopulatedFieldAndLocalityClaim()
    {
        await Task.Yield();
        // GIVEN classification and a separately cited locality.
        var source = GemReferenceSamples.Source("notableLocality");
        var content = GemReferenceSamples.Mineral() with
        {
            NotableLocality = new("Test hills", "Only known commercial source", source.ReviewedOn, source.Id),
            Sources = [.. GemReferenceSamples.Mineral().Sources, source]
        };
        // WHEN validated THEN its own claim source is accepted.
        Assert.Empty(Errors(content));
        // AND missing, displaced, or differently dated citations cannot substantiate that claim.
        Assert.Contains("notableLocality", Errors(content with { Sources = content.Sources.Where(s => s.Id != source.Id).ToArray() }).Keys);
        Assert.Contains("notableLocality", Errors(content with { NotableLocality = content.NotableLocality! with { SourceAssertionId = content.Sources[0].Id } }).Keys);
        Assert.Contains("notableLocality", Errors(content with { NotableLocality = content.NotableLocality! with { ReviewedOn = source.ReviewedOn.AddDays(-1) } }).Keys);
        Assert.Contains("sources", Errors(content with { Sources = [.. content.Sources, GemReferenceSamples.Source("description")] }).Keys);
        Assert.Contains("sources", Errors(content with { Sources = content.Sources.Where(s => s.Field != "species").ToArray() }).Keys);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("materialKind")]
    [InlineData("commonName")]
    [InlineData("description")]
    [InlineData("aliases")]
    [InlineData("sources")]
    [InlineData("url")]
    [InlineData("credentials")]
    [InlineData("future")]
    [InlineData("unknownField")]
    [InlineData("duplicateSource")]
    [InlineData("control")]
    public async Task RejectsInvalidReferenceContent(string invalid)
    {
        await Task.Yield();
        // GIVEN one malformed part of an otherwise valid reference.
        var content = GemReferenceSamples.Mineral();
        content = invalid switch
        {
            "id" => content with { Id = Guid.Empty },
            "materialKind" => content with { MaterialKind = "imitation" },
            "commonName" => content with { CommonName = new string('a', 201) },
            "description" => content with { Description = new string('a', 2001) },
            "aliases" => content with { Aliases = Enumerable.Range(0, 21).Select(n => n.ToString()).ToArray() },
            "sources" => content with { Sources = Enumerable.Range(0, 65).Select(_ => GemReferenceSamples.Source("commonName")).ToArray() },
            "url" => content with { Sources = [.. content.Sources.Skip(1), content.Sources[0] with { Url = "javascript:alert(1)" }] },
            "credentials" => content with { Sources = [.. content.Sources.Skip(1), content.Sources[0] with { Url = "https://user:pass@example.com/" }] },
            "future" => content with { Sources = [.. content.Sources.Skip(1), content.Sources[0] with { ReviewedOn = new(2026, 10, 2) }] },
            "unknownField" => content with { Sources = [.. content.Sources, GemReferenceSamples.Source("origin")] },
            "duplicateSource" => content with { Sources = [.. content.Sources, content.Sources[0]] },
            "control" => content with { CommonName = "Ru\nby" },
            _ => throw new InvalidOperationException(),
        };
        // WHEN validated THEN the malformed content is rejected.
        Assert.NotEmpty(Errors(content));
    }

    [Fact]
    public async Task IdentityNormalizesComparisonWithoutChangingDisplay()
    {
        await Task.Yield();
        // GIVEN equivalent Unicode spellings and ambiguous separator-bearing tuples.
        var content = GemReferenceSamples.Mineral();
        // WHEN identity is calculated THEN equivalents collide without altering display spelling.
        Assert.Equal(GemReferenceInput.IdentityKey(content with { CommonName = "  Ruby " }),
            GemReferenceInput.IdentityKey(content with { CommonName = "Ｒｕｂｙ" }));
        Assert.Equal(32, GemReferenceInput.IdentityKey(content).Length);
        Assert.Equal("Ｒｕｂｙ", GemReferenceInput.Normalize(content with { CommonName = " Ｒｕｂｙ " }).CommonName);
        Assert.False(GemReferenceInput.IdentityKey(content with { Group = "A|B", Species = "C" }).SequenceEqual(
            GemReferenceInput.IdentityKey(content with { Group = "A", Species = "B|C" })));
        Assert.Contains("aliases", Errors(content with
        {
            Aliases = ["Ruby", "Ｒｕｂｙ"],
            Sources = [.. content.Sources, GemReferenceSamples.Source("aliases")]
        }).Keys);
    }

    [Fact]
    public async Task RetirementPreservesIdentityAndRequiresSafeResolution()
    {
        await Task.Yield();
        // GIVEN retained shared identities and an existing redirect chain.
        var content = GemReferenceSamples.Mineral();
        var target = Guid.NewGuid();
        // WHEN retiring THEN a rationale or valid redirect is required without changing identity.
        Assert.Empty(Errors(content with { IsRetired = true, RetirementExplanation = "Reclassified" }));
        Assert.Contains("retirement", Errors(content with { IsRetired = true }).Keys);
        Assert.Contains("retirement", Errors(content with { RetirementExplanation = "Still active" }).Keys);
        Assert.Contains("retirement", Errors(content with { IsRetired = true, RedirectEntryId = content.Id }).Keys);
        Assert.Contains("retirement", Errors(content with { IsRetired = true, RedirectEntryId = target }).Keys);
        Assert.Contains("retirement", Errors(content with { IsRetired = true, RedirectEntryId = target },
            new Dictionary<Guid, Guid?> { [target] = content.Id }).Keys);
        Assert.Empty(Errors(content with { IsRetired = true, RedirectEntryId = target },
            new Dictionary<Guid, Guid?> { [target] = null }));
        Assert.Equal(GemReferenceInput.IdentityKey(content), GemReferenceInput.IdentityKey(content with { IsRetired = true }));
    }
}
