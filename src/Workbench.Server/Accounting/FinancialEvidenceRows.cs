// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Accounting;

public sealed class FinancialEvidenceSet
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public string OwnerKind { get; set; } = "";
    public Guid OwnerId { get; set; }
    public Guid OwnerRevisionId { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public Guid SupplierId { get; set; }
    public Guid ActorId { get; set; }
    public DateOnly PostingDate { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public byte[] SourceSnapshotSha256 { get; set; } = [];
    public string? MissingEvidenceReason { get; set; }
    public bool LegacyEvidence { get; set; }
    public string? MutationPermission { get; set; }
    public Guid? InheritedEvidenceSetId { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class FinancialEvidenceLink
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid EvidenceSetId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid AttachmentId { get; set; }
    public Guid RevisionId { get; set; }
    public string Sha256 { get; set; } = "";
    public long Length { get; set; }
    public string Label { get; set; } = "";
    public string MediaType { get; set; } = "";
    public string Extension { get; set; } = "";
    public Guid ConfigurationVersion { get; set; }
    public int? RetentionYears { get; set; }
    public string? RetentionRationale { get; set; }
    public DateTimeOffset AnchorAtUtc { get; set; }
    public DateTimeOffset? MinimumRetentionDeadlineUtc { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class FinancialEvidenceAddition
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid EvidenceSetId { get; set; }
    public Guid LinkId { get; set; }
    public Guid? ReplacesLinkId { get; set; }
    public Guid RequestId { get; set; }
    public Guid ActorId { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class FinancialEvidenceReceipt
{
    public Guid TenantId { get; set; }
    public Guid RequestId { get; set; }
    public string Operation { get; set; } = "";
    public Guid ActorId { get; set; }
    public Guid? EvidenceSetId { get; set; }
    public string CanonicalInput { get; set; } = "";
    public byte[] InputSha256 { get; set; } = [];
    public string ResultJson { get; set; } = "";
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class FinancialEvidenceAttachmentState
{
    public Guid TenantId { get; set; }
    public Guid AttachmentId { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
