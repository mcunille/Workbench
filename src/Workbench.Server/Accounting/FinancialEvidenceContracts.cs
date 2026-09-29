// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Accounting;

public sealed record FinancialEvidenceLinkResponse(Guid LinkId, Guid DocumentId, Guid RevisionId, string Sha256,
    long Length, string Label, DateTimeOffset? RetainUntilUtc, bool Indefinite, string Availability, Guid? ReplacesLinkId);
public sealed record FinancialEvidenceSetResponse(Guid Id, string? MissingEvidenceReason, FinancialEvidenceLinkResponse[] Links);
