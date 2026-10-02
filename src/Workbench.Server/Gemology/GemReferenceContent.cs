// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Gemology;

public sealed record GemReferenceContent(Guid Id, string MaterialKind, string CommonName,
    string? Group, string? Species, string? Variety, string? Description,
    IReadOnlyList<string> Aliases, IReadOnlyList<GemReferenceSourceContent> Sources,
    GemReferenceLocalityContent? NotableLocality, bool IsRetired,
    string? RetirementExplanation, Guid? RedirectEntryId);

public sealed record GemReferenceSourceContent(Guid Id, string Field, string Title,
    string Publisher, string? Url, string? Citation, DateOnly? AccessedOn, DateOnly ReviewedOn);

public sealed record GemReferenceLocalityContent(string Place, string Scope,
    DateOnly ReviewedOn, Guid SourceAssertionId);
