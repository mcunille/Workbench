// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Purchasing;

public sealed class SupplierOpenItem
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public Guid SupplierId { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public string Currency { get; set; } = "";
    public string SourceKind { get; set; } = "";
    public Guid SourceId { get; set; }
    public Guid SourceRevisionId { get; set; }
    public Guid? BillId { get; set; }
    public DateOnly SourcePostingDate { get; set; }
    public DateOnly? DueDate { get; set; }
    public string SourceSnapshotJson { get; set; } = "{}";
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class SupplierItemMovement
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid ItemId { get; set; }
    public Guid GroupId { get; set; }
    public string EventKind { get; set; } = "";
    public Guid SourceEventId { get; set; }
    public Guid? RecognitionEventId { get; set; }
    public DateOnly PostingDate { get; set; }
    public decimal Amount { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class SupplierApplication
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public Guid FundingItemId { get; set; }
    public Guid DebtItemId { get; set; }
    public DateOnly PostingDate { get; set; }
    public decimal Amount { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class SupplierApplicationReversal
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public Guid ApplicationId { get; set; }
    public DateOnly PostingDate { get; set; }
    public string Reason { get; set; } = "";
    public Guid ActorId { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class SupplierControlAttribution
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public Guid MovementId { get; set; }
    public Guid JournalId { get; set; }
    public int Ordinal { get; set; }
    public Guid AccountId { get; set; }
    public Guid AccountVersion { get; set; }
    public string AccountPurpose { get; set; } = "";
    public decimal Amount { get; set; }
}

public sealed class SupplierFinancialGroup
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public string Operation { get; set; } = "";
    public Guid SourceId { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class SupplierFinancialReceipt
{
    public Guid TenantId { get; set; }
    public Guid RequestId { get; set; }
    public Guid GroupId { get; set; }
    public string Operation { get; set; } = "";
    public Guid ActorId { get; set; }
    public string CanonicalInput { get; set; } = "{}";
    public byte[] InputSha256 { get; set; } = [];
    public string ResultJson { get; set; } = "{}";
    public DateTimeOffset RecordedAtUtc { get; set; }
}

// Coordination records are intentionally separate from immutable monetary evidence.
public sealed class SupplierItemVersion
{
    public Guid TenantId { get; set; }
    public Guid ItemId { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class SupplierApplicationVersion
{
    public Guid TenantId { get; set; }
    public Guid ApplicationId { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
