// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Purchasing;

public sealed class SupplierPayment
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid RevisionId { get; set; }
    public Guid SupplierId { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public string Currency { get; set; } = "";
    public DateOnly PaymentDate { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public DateOnly PostingDate { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = "";
    public Guid FundingAccountId { get; set; }
    public Guid FundingAccountVersion { get; set; }
    public string FundingAccountPurpose { get; set; } = "";
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    // Versioned immutable payment/source snapshot: stored account and party metadata,
    // private document revisions or explicit missing evidence, and immediate allocations.
    public string EvidenceJson { get; set; } = "{}";
    public Guid ActorId { get; set; }
    public Guid GroupId { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class SupplierPaymentCorrection
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid OriginalPaymentId { get; set; }
    public Guid? ReplacementPaymentId { get; set; }
    public Guid GroupId { get; set; }
    public string Reason { get; set; } = "";
    public Guid ActorId { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class SupplierPaymentVersion
{
    public Guid TenantId { get; set; }
    public Guid PaymentId { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
