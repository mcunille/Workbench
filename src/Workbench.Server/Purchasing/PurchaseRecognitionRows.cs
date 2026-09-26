// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Purchasing;

public sealed class RecognitionUnit
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public int PurchaseOrderRevision { get; set; }
    public Guid SupplierId { get; set; }
    public string Currency { get; set; } = "";
    public string GoodsReference { get; set; } = "";
    public string Classification { get; set; } = "";
    public decimal Quantity { get; set; }
    public string QuantityUnit { get; set; } = "";
    public int PolicyVersion { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class RecognitionSideEvent
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid UnitId { get; set; }
    public string Side { get; set; } = "";
    public int EventRevision { get; set; }
    public Guid SourceId { get; set; }
    public Guid SourceRevision { get; set; }
    public string SourceComponentKey { get; set; } = "";
    public string SubdivisionKey { get; set; } = "";
    public decimal SourceQuantity { get; set; }
    public decimal SourceAmount { get; set; }
    public DateOnly DocumentDate { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public DateOnly PostingDate { get; set; }
    public Guid ConfigurationVersion { get; set; }
    public Guid ActorId { get; set; }
    public string EvidenceJson { get; set; } = "";
    public byte[] EvidenceSha256 { get; set; } = [];
    public Guid? JournalId { get; set; }
    public Guid? CorrectionGroupId { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class RecognitionComponent
{
    public Guid TenantId { get; set; }
    public Guid EventId { get; set; }
    public string ComponentKey { get; set; } = "";
    public string Kind { get; set; } = "";
    public decimal Amount { get; set; }
    public string? Reason { get; set; }
    public string? AssignedCostComponentKey { get; set; }
}

public sealed class RecognitionMatch
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid UnitId { get; set; }
    public Guid RecognitionEventId { get; set; }
    public Guid InvoiceEventId { get; set; }
    public Guid? CorrectionGroupId { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class RecognitionCorrectionGroup
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid UnitId { get; set; }
    public Guid ActorId { get; set; }
    public string Operation { get; set; } = "";
    public DateOnly PostingDate { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class RecognitionGroupReceipt
{
    public Guid TenantId { get; set; }
    public Guid RequestId { get; set; }
    public Guid ActorId { get; set; }
    public string CommandKind { get; set; } = "";
    public int CommandVersion { get; set; }
    public string CanonicalInput { get; set; } = "";
    public byte[] InputSha256 { get; set; } = [];
    public string ResultJson { get; set; } = "";
    public DateTimeOffset RecordedAtUtc { get; set; }
}
