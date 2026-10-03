// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Gemology;

public sealed record GemReferenceDraftSelection(Guid DraftId, string ExpectedDraftRowVersion);
public sealed record GemReferenceDraftSaveRequest(Guid EntryId, GemReferenceContent Content,
    string? ExpectedDraftRowVersion, string? ExpectedPublishedRowVersion);
public sealed record GemReferenceDraftResponse(Guid Id, Guid EntryId, GemReferenceContent Content,
    string? ExpectedPublishedRowVersion, string RowVersion, Guid CreatedBy, Guid UpdatedBy,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, IReadOnlyDictionary<string, string[]> Errors);
public sealed record GemReferenceDraftPage(IReadOnlyList<GemReferenceDraftResponse> Drafts, string? NextCursor);
public sealed record GemReferenceFieldChange(string Field, object? Before, object? After);
public sealed record GemReferenceEntryReview(Guid DraftId, Guid EntryId, bool IsStale,
    IReadOnlyList<GemReferenceFieldChange> Changes, IReadOnlyDictionary<string, string[]> Errors);
public sealed record GemReferenceReviewRequest(IReadOnlyList<GemReferenceDraftSelection> Drafts);
public sealed record GemReferenceReviewResponse(IReadOnlyList<GemReferenceEntryReview> Entries);
public sealed record GemReferencePublishRequest(Guid RequestId, IReadOnlyList<GemReferenceDraftSelection> Drafts);
public sealed record GemReferencePublishedEntry(Guid EntryId, string RowVersion);
public sealed record GemReferencePublishOutcome(Guid RequestId, string Code,
    IReadOnlyList<GemReferencePublishedEntry> Entries, IReadOnlyList<GemReferenceEntryReview> Review);
public sealed record GemReferencePublicationAuditResponse(Guid Id, Guid RequestId, Guid AccountId,
    string Outcome, string SummaryJson, DateTimeOffset CreatedAtUtc);
public sealed record GemReferencePublicationAuditPage(IReadOnlyList<GemReferencePublicationAuditResponse> Events, string? NextCursor);
