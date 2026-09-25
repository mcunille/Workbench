// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Accounting;

public sealed class AccountingPeriod
{
    public Guid TenantId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public DateOnly FiscalYearStart { get; set; }
    public Guid ConfigurationVersion { get; set; }
    public string Currency { get; set; } = "";
    public int Scale { get; set; }
    public int FiscalStartMonth { get; set; }
    public string StartApproach { get; set; } = "";
    public DateOnly AccountingStartDate { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class AccountingPeriodClosure
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public Guid ActorId { get; set; }
    public string Reason { get; set; } = "";
    public string EvidenceJson { get; set; } = "";
    public byte[] EvidenceSha256 { get; set; } = [];
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class AccountingPeriodCloseReceipt
{
    public Guid TenantId { get; set; }
    public Guid RequestId { get; set; }
    public Guid ActorId { get; set; }
    public string CommandKind { get; set; } = "";
    public int CommandVersion { get; set; }
    public string CanonicalInput { get; set; } = "";
    public byte[] InputSha256 { get; set; } = [];
    public Guid ClosureId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}
