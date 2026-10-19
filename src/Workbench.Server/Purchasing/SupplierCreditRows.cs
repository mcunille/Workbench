// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Purchasing;

public sealed class SupplierCredit
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid RevisionId { get; set; }
    public Guid SupplierId { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public string Currency { get; set; } = "";
    public DateOnly DocumentDate { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public DateOnly PostingDate { get; set; }
    public decimal Total { get; set; }
    public string Reference { get; set; } = "";
    public string NormalizedReference { get; set; } = "";
    public string Reason { get; set; } = "";
    public string SourceSnapshotJson { get; set; } = "{}";
    public Guid ActorId { get; set; }
    public Guid GroupId { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class SupplierCreditClaim
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid CreditId { get; set; }
    public Guid BillId { get; set; }
    public Guid BillRevisionId { get; set; }
    public Guid RecognitionEventId { get; set; }
    public string ComponentKey { get; set; } = "";
    public string Treatment { get; set; } = "";
    public decimal Amount { get; set; }
    public Guid AccountId { get; set; }
    public Guid AccountVersion { get; set; }
    public Guid OriginalJournalId { get; set; }
    public int OriginalJournalOrdinal { get; set; }
}

public sealed class SupplierCreditVersion
{
    public Guid TenantId { get; set; }
    public Guid CreditId { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
