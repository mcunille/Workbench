// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Accounting;

public sealed class JournalPolicyFreeze
{
    public Guid TenantId { get; set; }
    public Guid ConfigurationVersion { get; set; }
    public string Currency { get; set; } = "";
    public int Scale { get; set; }
    public int FiscalStartMonth { get; set; }
    public string StartApproach { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public Guid FirstJournalId { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class JournalSourceEvent
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string SourceKind { get; set; } = "";
    public Guid SourceId { get; set; }
    public Guid SourceRevision { get; set; }
    public string EventKind { get; set; } = "";
    public int RuleVersion { get; set; }
    public Guid ActorId { get; set; }
    public DateOnly DocumentDate { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public DateOnly PostingDate { get; set; }
    public string? Reference { get; set; }
    public string? Reason { get; set; }
    public string SnapshotJson { get; set; } = "";
    public byte[] SnapshotSha256 { get; set; } = [];
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class JournalEntry
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public long Sequence { get; set; }
    public Guid SourceEventId { get; set; }
    public Guid ConfigurationVersion { get; set; }
    public string Currency { get; set; } = "";
    public int Scale { get; set; }
    public DateOnly DocumentDate { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public DateOnly PostingDate { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public Guid ActorId { get; set; }
    public string? Reference { get; set; }
    public string? Reason { get; set; }
    public decimal DebitTotal { get; set; }
    public decimal CreditTotal { get; set; }
}

public sealed class JournalLineRow
{
    public Guid TenantId { get; set; }
    public Guid JournalId { get; set; }
    public int Ordinal { get; set; }
    public Guid AccountId { get; set; }
    public Guid AccountVersion { get; set; }
    public string AccountCode { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string AccountType { get; set; } = "";
    public string AccountPurpose { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
}

public sealed class JournalPostingReceipt
{
    public Guid TenantId { get; set; }
    public Guid RequestId { get; set; }
    public Guid ActorId { get; set; }
    public string SourceCommandKind { get; set; } = "";
    public int SourceCommandVersion { get; set; }
    public string CanonicalInput { get; set; } = "";
    public byte[] InputSha256 { get; set; } = [];
    public Guid SourceEventId { get; set; }
    public Guid JournalId { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}
