// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Gemology;

public sealed record GemReferenceListEntry(Guid Id, string MaterialKind, string CommonName,
    string? Group, string? Species, string? Variety, string Layer)
{
    public bool NeedsReview { get; init; }
    public IReadOnlyDictionary<string, string[]> ReviewReasons { get; init; } = new Dictionary<string, string[]>();
}
public sealed record GemReferencePageResponse(IReadOnlyList<GemReferenceListEntry> Entries, string? NextCursor);
public sealed record GemReferenceSourceResponse(Guid Id, string Field, string Title, string Publisher,
    string? Url, string? Citation, DateOnly? AccessedOn, DateOnly ReviewedOn, string Attribution);
public sealed record GemReferenceRetirementResponse(bool IsRetired, string? Explanation, Guid? RedirectEntryId);
public sealed record GemReferenceDetailResponse(Guid Id, string MaterialKind, string CommonName,
    string? Group, string? Species, string? Variety, string Layer, IReadOnlyList<string> Aliases,
    string? Description, string RowVersion, IReadOnlyList<GemReferenceSourceResponse> SourceAssertions,
    GemReferenceLocalityContent? NotableLocality, GemReferenceRetirementResponse Retirement)
{
    public GemReferenceEffectiveVersion? EffectiveVersion { get; init; }
    public IReadOnlyDictionary<string, GemReferenceEffectiveField> EffectiveFields { get; init; } = new Dictionary<string, GemReferenceEffectiveField>();
    public GemReferenceContent? WorkbenchContent { get; init; }
    public IReadOnlyDictionary<string, GemReferenceFieldOverride> Overrides { get; init; } = new Dictionary<string, GemReferenceFieldOverride>();
    public bool IsArchived { get; init; }
    public bool NeedsReview { get; init; }
    public IReadOnlyDictionary<string, string[]> ReviewReasons { get; init; } = new Dictionary<string, string[]>();
}
