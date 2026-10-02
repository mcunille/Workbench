// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Gemology;

public sealed record GemReferenceListEntry(Guid Id, string MaterialKind, string CommonName,
    string? Group, string? Species, string? Variety, string Layer);
public sealed record GemReferencePageResponse(IReadOnlyList<GemReferenceListEntry> Entries, string? NextCursor);
public sealed record GemReferenceSourceResponse(Guid Id, string Field, string Title, string Publisher,
    string? Url, string? Citation, DateOnly? AccessedOn, DateOnly ReviewedOn, string Attribution);
public sealed record GemReferenceRetirementResponse(bool IsRetired, string? Explanation, Guid? RedirectEntryId);
public sealed record GemReferenceDetailResponse(Guid Id, string MaterialKind, string CommonName,
    string? Group, string? Species, string? Variety, string Layer, IReadOnlyList<string> Aliases,
    string? Description, string RowVersion, IReadOnlyList<GemReferenceSourceResponse> SourceAssertions,
    GemReferenceLocalityContent? NotableLocality, GemReferenceRetirementResponse Retirement);
