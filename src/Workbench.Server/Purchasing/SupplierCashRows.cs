// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Purchasing;

public sealed class SupplierCashSource
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid RevisionId { get; set; }
    public string Kind { get; set; } = "";
    public Guid SupplierId { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public string Currency { get; set; } = "";
    public DateOnly ActualDate { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public DateOnly PostingDate { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = "";
    public Guid FundingAccountId { get; set; }
    public Guid FundingAccountVersion { get; set; }
    public string FundingAccountPurpose { get; set; } = "";
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public string SourceSnapshotJson { get; set; } = "{}";
    public Guid ActorId { get; set; }
    public Guid GroupId { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class SupplierCashSettlement
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid CashSourceId { get; set; }
    public Guid ItemId { get; set; }
    public decimal Amount { get; set; }
    public Guid MovementId { get; set; }
    public Guid JournalId { get; set; }
    public int JournalOrdinal { get; set; }
    public Guid AccountId { get; set; }
    public Guid AccountVersion { get; set; }
}

public sealed class SupplierCashVersion
{
    public Guid TenantId { get; set; }
    public Guid CashSourceId { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
