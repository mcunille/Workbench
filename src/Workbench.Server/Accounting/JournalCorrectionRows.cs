// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Accounting;

public sealed class JournalCorrectionGroup
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid OriginalSourceEventId { get; set; }
    public Guid OriginalJournalId { get; set; }
    public Guid ReversalSourceEventId { get; set; }
    public Guid ReversalJournalId { get; set; }
    public Guid ReversalRequestId { get; set; }
    public Guid? ReplacementSourceEventId { get; set; }
    public Guid? ReplacementJournalId { get; set; }
    public Guid? ReplacementRequestId { get; set; }
    public Guid? ReplacementSourceRevision { get; set; }
    public Guid ActorId { get; set; }
    public DateOnly PostingDate { get; set; }
    public string Reason { get; set; } = "";
    public string EvidenceJson { get; set; } = "";
    public byte[] EvidenceSha256 { get; set; } = [];
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class JournalCorrectionReceipt
{
    public Guid TenantId { get; set; }
    public Guid RequestId { get; set; }
    public Guid ActorId { get; set; }
    public string SourceCommandKind { get; set; } = "";
    public int SourceCommandVersion { get; set; }
    public string CanonicalInput { get; set; } = "";
    public byte[] InputSha256 { get; set; } = [];
    public Guid CorrectionId { get; set; }
}
