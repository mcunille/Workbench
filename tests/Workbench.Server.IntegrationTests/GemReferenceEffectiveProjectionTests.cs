// Copyright (c) 2026 The White Stag Collection.

using System.Text.Json;
using Workbench.Server.Gemology;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class GemReferenceEffectiveProjectionTests
{
    private static readonly DateOnly Today = new(2026, 10, 1);
    private static GemReferenceFieldOverride Replace<T>(T value, params GemReferenceSourceContent[] sources) =>
        new("replace", JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), sources);
    private static GemReferenceDetailResponse Resolve(GemReferenceContent shared,
        IReadOnlyDictionary<string, GemReferenceFieldOverride>? overrides = null) =>
        GemReferenceEffectiveProjection.Resolve(shared, "shared-2", overrides ?? new Dictionary<string, GemReferenceFieldOverride>(), "tenant-1", Today);

    [Fact]
    public async Task UnchangedSharedDetailPreservesSourceAssertionOrdering()
    {
        await Task.Yield();
        // GIVEN unchanged shared citations stored in a different field and ID order than the detail contract.
        var laterName = GemReferenceSamples.Source("commonName") with { Id = Guid.Parse("ffffffff-0000-0000-0000-000000000001") };
        var earlierName = GemReferenceSamples.Source("commonName") with { Id = Guid.Parse("00000001-0000-0000-0000-ffffffffffff") };
        var material = GemReferenceSamples.Source("materialKind");
        var shared = GemReferenceSamples.Mineral() with { Sources = [material, laterName, earlierName] };
        // WHEN projected without choices THEN top-level citations retain ordinal field followed by .NET Guid order.
        var detail = Resolve(shared);
        Assert.Equal(new[] { earlierName.Id, laterName.Id, material.Id }, detail.SourceAssertions.Select(source => source.Id));
        Assert.All(detail.SourceAssertions, source => Assert.Equal("workbench", source.Attribution));
    }

    [Fact]
    public async Task InheritedCorrectionMovesValueAndSourcesTogether()
    {
        await Task.Yield();
        // GIVEN a current shared correction with a newly reviewed citation.
        var source = GemReferenceSamples.Source("commonName") with { Title = "Corrected name", ReviewedOn = Today, AccessedOn = Today.AddDays(-1) };
        var shared = GemReferenceSamples.Mineral() with { CommonName = "Corrected ruby", Sources = [.. GemReferenceSamples.Mineral().Sources.Where(s => s.Field != "commonName"), source] };
        // WHEN the tenant inherits THEN value and current citation resolve together.
        var result = Resolve(shared);
        Assert.Equal("Corrected ruby", result.CommonName);
        Assert.Equal("inherit", result.EffectiveFields["commonName"].State);
        Assert.Equal("workbench", result.EffectiveFields["commonName"].Attribution);
        var assertion = Assert.Single(result.EffectiveFields["commonName"].Sources);
        Assert.Equal(source.Id, assertion.Id);
        Assert.Equal("Corrected name", assertion.Title);
        Assert.Equal(Today, assertion.ReviewedOn);
        Assert.Equal(Today.AddDays(-1), assertion.AccessedOn);
        Assert.Equal("workbench", assertion.Attribution);
        Assert.Equal(new("shared-2", "tenant-1"), result.EffectiveVersion);
        Assert.Equal("shared-2", result.RowVersion);
        Assert.Equal("workbenchReference", result.Layer);
    }

    [Fact]
    public async Task ReplacementNeverKeepsWorkbenchSources()
    {
        await Task.Yield();
        // GIVEN a tenant name assertion and an unsourced tenant species choice.
        var source = GemReferenceSamples.Source("commonName") with { ReviewedOn = Today, AccessedOn = null };
        var overrides = new Dictionary<string, GemReferenceFieldOverride>
        { ["commonName"] = Replace("Personal ruby", source), ["species"] = Replace("Corundum") };
        // WHEN replaced THEN the displaced shared citations are absent.
        var result = Resolve(GemReferenceSamples.Mineral(), overrides);
        Assert.Equal("Personal ruby", result.CommonName);
        Assert.Equal("Corundum", result.Species);
        Assert.Equal("workbenchReferenceCustomized", result.Layer);
        Assert.Equal("replace", result.EffectiveFields["commonName"].State);
        Assert.Equal("tenant", result.EffectiveFields["commonName"].Attribution);
        var assertion = Assert.Single(result.SourceAssertions, s => s.Field == "commonName");
        Assert.Equal(source.Id, assertion.Id);
        Assert.Equal(Today, assertion.ReviewedOn);
        Assert.Null(assertion.AccessedOn);
        Assert.Equal("tenant", assertion.Attribution);
        Assert.Empty(result.EffectiveFields["species"].Sources);
        Assert.False(result.NeedsReview);
        Assert.Equal("Ruby", result.WorkbenchContent!.CommonName);
    }

    [Fact]
    public async Task ClearAndResetHaveDifferentSemantics()
    {
        await Task.Yield();
        // GIVEN a shared variety and a tenant deliberate clear.
        var shared = GemReferenceSamples.Mineral();
        var cleared = Resolve(shared, new Dictionary<string, GemReferenceFieldOverride> { ["variety"] = new("clear", null, []) });
        // WHEN cleared THEN both the value and its shared assertion disappear.
        Assert.Null(cleared.Variety);
        Assert.Equal("clear", cleared.EffectiveFields["variety"].State);
        Assert.Equal("tenant", cleared.EffectiveFields["variety"].Attribution);
        Assert.DoesNotContain(cleared.SourceAssertions, s => s.Field == "variety");
        // WHEN reset to inherit THEN the current shared value and citation return.
        var reset = Resolve(shared, new Dictionary<string, GemReferenceFieldOverride> { ["variety"] = new("inherit", null, []) });
        Assert.Equal("Ruby", reset.Variety);
        Assert.Equal("workbench", Assert.Single(reset.EffectiveFields["variety"].Sources).Attribution);
        Assert.Empty(reset.Overrides);
        Assert.Equal("workbenchReference", reset.Layer);
        Assert.Equal("tenant-1", reset.EffectiveVersion!.TenantRowVersion);
    }

    [Fact]
    public async Task MineralTransitionRetainsInvalidSpeciesClear()
    {
        await Task.Yield();
        // GIVEN a retained species clear after the shared entry becomes mineral.
        var choice = new GemReferenceFieldOverride("clear", null, []);
        var overrides = new Dictionary<string, GemReferenceFieldOverride> { ["species"] = choice };
        // WHEN resolving THEN the raw choice survives and species needs review.
        var result = Resolve(GemReferenceSamples.Mineral(), overrides);
        Assert.Equal(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), result.Id);
        Assert.Null(result.Species);
        Assert.Equal(choice, result.Overrides["species"]);
        Assert.Equal("clear", result.EffectiveFields["species"].State);
        Assert.Empty(result.EffectiveFields["species"].Sources);
        Assert.True(result.NeedsReview);
        Assert.NotEmpty(result.ReviewReasons["species"]);
    }

    [Theory]
    [InlineData("replace", "12")]
    [InlineData("replace", "null")]
    [InlineData("unknown", "\"Personal name\"")]
    [InlineData("clear", "null")]
    [InlineData("inherit", "\"Personal name\"")]
    [InlineData(null, "null")]
    public async Task UnapplicableChoiceKeepsFallbackValueWithWorkbenchProvenance(string? state, string json)
    {
        await Task.Yield();
        // GIVEN a retained name choice whose state or value cannot represent an applied tenant name.
        var source = GemReferenceSamples.Source("commonName") with { ReviewedOn = Today, AccessedOn = Today.AddDays(-1) };
        var shared = GemReferenceSamples.Mineral() with
        { Sources = [.. GemReferenceSamples.Mineral().Sources.Where(s => s.Field != "commonName"), source] };
        var choice = state is null ? null! : new GemReferenceFieldOverride(state, JsonDocument.Parse(json).RootElement.Clone(), []);
        // WHEN resolved THEN the displayed fallback retains its actual value, ownership, and current source.
        var result = Resolve(shared, new Dictionary<string, GemReferenceFieldOverride> { ["commonName"] = choice });
        Assert.Equal("Ruby", result.CommonName);
        Assert.Equal("invalid", result.EffectiveFields["commonName"].State);
        Assert.Equal("workbench", result.EffectiveFields["commonName"].Attribution);
        var assertion = Assert.Single(result.EffectiveFields["commonName"].Sources);
        Assert.Equal(source.Id, assertion.Id);
        Assert.Equal(Today, assertion.ReviewedOn);
        Assert.Equal(Today.AddDays(-1), assertion.AccessedOn);
        Assert.Equal("workbench", assertion.Attribution);
        Assert.Equal(assertion, Assert.Single(result.SourceAssertions, s => s.Field == "commonName"));
        // AND the raw sparse choice and field review reason survive for explicit reconciliation.
        Assert.Equal(choice, result.Overrides["commonName"]);
        Assert.True(result.NeedsReview);
        Assert.NotEmpty(result.ReviewReasons["commonName"]);
    }

    [Fact]
    public async Task TenantLocalityRequiresItsOwnMatchingSource()
    {
        await Task.Yield();
        // GIVEN otherwise valid unsourced tenant content and a locality with its own assertion.
        var source = GemReferenceSamples.Source("notableLocality");
        var content = GemReferenceSamples.Mineral() with { Sources = [] };
        var locality = new GemReferenceLocalityContent("Test hills", "Commercial source", source.ReviewedOn, source.Id);
        // WHEN validated THEN ordinary unsourced claims are valid but locality is source-bound.
        Assert.Empty(GemReferenceTenantInput.Validate(content, Today));
        Assert.Contains("notableLocality", GemReferenceTenantInput.Validate(content with { NotableLocality = locality }, Today).Keys);
        Assert.Empty(GemReferenceTenantInput.Validate(content with { NotableLocality = locality, Sources = [source] }, Today));
        foreach (var mismatch in new[] { source with { Field = "commonName" }, source with { ReviewedOn = Today } })
            Assert.Contains("notableLocality", GemReferenceTenantInput.Validate(content with { NotableLocality = locality, Sources = [mismatch] }, Today).Keys);
        // AND a replacement cannot borrow the current shared locality assertion.
        var shared = content with { NotableLocality = locality, Sources = [source] };
        var result = Resolve(shared, new Dictionary<string, GemReferenceFieldOverride> { ["notableLocality"] = Replace(locality) });
        Assert.Equal(locality, result.NotableLocality);
        Assert.Empty(result.EffectiveFields["notableLocality"].Sources);
        Assert.Contains("notableLocality", result.ReviewReasons.Keys);
    }

    [Theory]
    [InlineData("unknown", "replace", "\"Ruby\"")]
    [InlineData("commonName", "invalid", "\"Ruby\"")]
    [InlineData("commonName", "clear", "null")]
    [InlineData("aliases", "clear", "null")]
    [InlineData("commonName", "replace", "\" \"")]
    [InlineData("species", "replace", "null")]
    [InlineData("commonName", "replace", "12")]
    [InlineData("aliases", "replace", "\"Ruby\"")]
    [InlineData("aliases", "replace", "[12]")]
    [InlineData("aliases", "replace", "[\"Ruby\",\"Ｒｕｂｙ\"]")]
    [InlineData("materialKind", "replace", "\"imitation\"")]
    [InlineData("notableLocality", "replace", "{}")]
    [InlineData("description", "clear", "\"value\"")]
    [InlineData("description", "inherit", "\"value\"")]
    public async Task RejectsMalformedOverrideChoices(string field, string state, string json)
    {
        await Task.Yield();
        // GIVEN one malformed explicit field choice.
        var overrides = new Dictionary<string, GemReferenceFieldOverride> { [field] = new(state, JsonDocument.Parse(json).RootElement.Clone(), []) };
        // WHEN validated THEN the field choice is rejected without substituting inherited data.
        Assert.Contains(field, GemReferenceTenantInput.ValidateOverrides(overrides, Today).Keys);
    }

    [Theory]
    [InlineData("oversizedAliases")]
    [InlineData("oversizedSources")]
    [InlineData("unsafeUrl")]
    [InlineData("credentials")]
    [InlineData("duplicateSource")]
    [InlineData("futureReview")]
    [InlineData("futureAccess")]
    [InlineData("wrongField")]
    [InlineData("clearSources")]
    [InlineData("inheritSources")]
    public async Task RejectsMalformedOverrideSourcesAndLimits(string invalid)
    {
        await Task.Yield();
        // GIVEN malformed provenance or an oversized replacement.
        var source = GemReferenceSamples.Source("commonName");
        var choice = invalid switch
        {
            "oversizedAliases" => Replace(Enumerable.Range(0, 21).Select(n => n.ToString()).ToArray()),
            "oversizedSources" => Replace("Name", Enumerable.Range(0, 65).Select(_ => GemReferenceSamples.Source("commonName")).ToArray()),
            "unsafeUrl" => Replace("Name", source with { Url = "javascript:alert(1)" }),
            "credentials" => Replace("Name", source with { Url = "https://user:pass@example.com" }),
            "duplicateSource" => Replace("Name", source, source),
            "futureReview" => Replace("Name", source with { ReviewedOn = Today.AddDays(1) }),
            "futureAccess" => Replace("Name", source with { AccessedOn = Today.AddDays(1) }),
            "wrongField" => Replace("Name", source with { Field = "species" }),
            "clearSources" => new GemReferenceFieldOverride("clear", null, [source with { Field = "description" }]),
            "inheritSources" => new GemReferenceFieldOverride("inherit", null, [source]),
            _ => throw new InvalidOperationException(),
        };
        var field = invalid == "oversizedAliases" ? "aliases" : invalid == "clearSources" ? "description" : "commonName";
        // WHEN validated THEN invalid tenant assertions cannot smuggle attribution.
        Assert.Contains(field, GemReferenceTenantInput.ValidateOverrides(new Dictionary<string, GemReferenceFieldOverride> { [field] = choice }, Today).Keys);
    }

    [Fact]
    public async Task EffectiveIdentityUsesUnicodeComparisonAndExcludesAliases()
    {
        await Task.Yield();
        // GIVEN Unicode-equivalent names supplied through replacement.
        var shared = GemReferenceSamples.Mineral();
        var result = Resolve(shared, new Dictionary<string, GemReferenceFieldOverride> { ["commonName"] = Replace(" Ｒｕｂｙ "), ["aliases"] = Replace(new[] { "Personal name" }) });
        // WHEN the effective identity is compared THEN compatibility spellings collide and aliases do not participate.
        var effective = shared with { CommonName = result.CommonName, Aliases = result.Aliases };
        Assert.Equal(GemReferenceInput.IdentityKey(shared), GemReferenceInput.IdentityKey(effective));
        Assert.Equal("Ｒｕｂｙ", result.CommonName);
        Assert.Equal(new[] { "Personal name" }, result.Aliases);
    }

    [Fact]
    public async Task TenantEntryPreservesArchiveVersionAndSourceAttribution()
    {
        await Task.Yield();
        // GIVEN an archived tenant addition with a sourced classification and unsourced name.
        var source = GemReferenceSamples.Source("species");
        var content = GemReferenceSamples.Mineral() with { Sources = [source] };
        // WHEN resolved THEN its provenance and tenant-only concurrency token remain visible.
        var result = GemReferenceEffectiveProjection.ResolveTenant(content, "tenant-3", true, Today);
        Assert.Equal("tenantEntry", result.Layer);
        Assert.True(result.IsArchived);
        Assert.Equal(new(null, "tenant-3"), result.EffectiveVersion);
        Assert.Equal("tenant-3", result.RowVersion);
        Assert.Equal("replace", result.EffectiveFields["species"].State);
        Assert.Equal("tenant", result.EffectiveFields["species"].Attribution);
        Assert.Equal(source.Id, Assert.Single(result.EffectiveFields["species"].Sources).Id);
        Assert.Equal(source.ReviewedOn, Assert.Single(result.SourceAssertions).ReviewedOn);
        Assert.Empty(result.EffectiveFields["commonName"].Sources);
        Assert.Null(result.WorkbenchContent);
        Assert.Empty(result.Overrides);
        Assert.False(result.NeedsReview);
    }

    [Theory]
    [InlineData("materialKind")]
    [InlineData("commonName")]
    [InlineData("group")]
    [InlineData("species")]
    [InlineData("variety")]
    [InlineData("description")]
    [InlineData("aliases")]
    [InlineData("sources")]
    [InlineData("retirement")]
    public async Task TenantAuthoringRetainsFieldValidation(string field)
    {
        await Task.Yield();
        // GIVEN unsourced tenant claims with one invalid field.
        var content = GemReferenceSamples.Mineral() with { Sources = [] };
        content = field switch
        {
            "materialKind" => content with { MaterialKind = "imitation" },
            "commonName" => content with { CommonName = "Ru\nby" },
            "group" => content with { Group = new string('a', 201) },
            "species" => content with { Species = null },
            "variety" => content with { Variety = " " },
            "description" => content with { Description = new string('a', 2001) },
            "aliases" => content with { Aliases = ["\u00a8Ruby", " \u0308Ruby"] },
            "sources" => content with { Sources = [GemReferenceSamples.Source("commonName") with { Url = "file:///etc/passwd" }] },
            "retirement" => content with { IsRetired = true, RetirementExplanation = "Removed" },
            _ => throw new InvalidOperationException(),
        };
        // WHEN validated THEN unsourced permission does not relax shape or classification rules.
        Assert.Contains(field, GemReferenceTenantInput.Validate(content, Today).Keys);
    }

    [Fact]
    public async Task NullSourceAssertionsAreRejectedWithoutCrashing()
    {
        await Task.Yield();
        // GIVEN a JSON-bound replacement containing a null source assertion.
        var choice = Replace("Personal ruby", new GemReferenceSourceContent[] { null! });
        // WHEN validated THEN malformed provenance is a field error.
        Assert.Contains("commonName", GemReferenceTenantInput.ValidateOverrides(
            new Dictionary<string, GemReferenceFieldOverride> { ["commonName"] = choice }, Today).Keys);
    }

    [Fact]
    public async Task RetainedMalformedCollectionsExposeReviewRatherThanCrash()
    {
        await Task.Yield();
        // GIVEN a tenant entry containing malformed stored collection elements.
        var content = GemReferenceSamples.Mineral() with { Aliases = [null!], Sources = [null!] };
        // WHEN read THEN the entry remains identifiable and exposes field-level review reasons.
        var result = GemReferenceEffectiveProjection.ResolveTenant(content, "tenant-4", false, Today);
        Assert.Equal(content.Id, result.Id);
        Assert.True(result.NeedsReview);
        Assert.Contains("aliases", result.ReviewReasons.Keys);
        Assert.Contains("sources", result.ReviewReasons.Keys);
    }

    [Fact]
    public async Task LocalityAndAliasesReplacementRemoveDisplacedSources()
    {
        await Task.Yield();
        // GIVEN a shared sourced alias list and locality, both deliberately removed by the tenant.
        var source = GemReferenceSamples.Source("notableLocality");
        var shared = GemReferenceSamples.Mineral() with
        {
            Aliases = ["Red corundum"],
            NotableLocality = new("Test hills", "Commercial source", source.ReviewedOn, source.Id),
            Sources = [.. GemReferenceSamples.Mineral().Sources, source, GemReferenceSamples.Source("aliases")],
        };
        // WHEN aliases are replaced with an empty list and locality cleared THEN neither keeps shared citations.
        var result = Resolve(shared, new Dictionary<string, GemReferenceFieldOverride>
        { ["aliases"] = Replace(Array.Empty<string>()), ["notableLocality"] = new("clear", null, []) });
        Assert.Empty(result.Aliases);
        Assert.Null(result.NotableLocality);
        Assert.Empty(result.EffectiveFields["aliases"].Sources);
        Assert.Equal("replace", result.EffectiveFields["aliases"].State);
        Assert.Equal("tenant", result.EffectiveFields["aliases"].Attribution);
        Assert.Empty(result.EffectiveFields["notableLocality"].Sources);
        Assert.DoesNotContain(result.SourceAssertions, s => s.Field is "aliases" or "notableLocality");
        Assert.False(result.NeedsReview);
    }
}
