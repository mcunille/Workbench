// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Gemology;

public sealed class GemReferenceEntry
{
    public Guid Id { get; set; }
    public string MaterialKind { get; set; } = "";
    public string CommonName { get; set; } = "";
    public string? Group { get; set; }
    public string? Species { get; set; }
    public string? Variety { get; set; }
    public string? Description { get; set; }
    public byte[] IdentityKey { get; set; } = [];
    public bool IsRetired { get; set; }
    public string? RetirementExplanation { get; set; }
    public Guid? RedirectEntryId { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public List<GemReferenceAlias> Aliases { get; set; } = [];
    public List<GemReferenceSourceAssertion> SourceAssertions { get; set; } = [];
    public GemReferenceLocalityAssertion? NotableLocality { get; set; }
}

public sealed class GemReferenceAlias
{
    public Guid EntryId { get; set; }
    public int Position { get; set; }
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
}

public sealed class GemReferenceSourceAssertion
{
    public Guid Id { get; set; }
    public Guid EntryId { get; set; }
    public string Field { get; set; } = "";
    public string Title { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string? Url { get; set; }
    public string? Citation { get; set; }
    public DateOnly? AccessedOn { get; set; }
    public DateOnly ReviewedOn { get; set; }
}

public sealed class GemReferenceLocalityAssertion
{
    public Guid EntryId { get; set; }
    public string Place { get; set; } = "";
    public string Scope { get; set; } = "";
    public DateOnly ReviewedOn { get; set; }
    public Guid SourceAssertionId { get; set; }
    public string SourceField { get; set; } = "notableLocality";
}
