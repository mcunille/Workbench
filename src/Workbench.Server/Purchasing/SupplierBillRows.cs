// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Purchasing;

public sealed class SupplierBill
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public Guid SupplierId { get; set; }
    public string Currency { get; set; } = "";
    public Guid? CurrentRevisionId { get; set; }
    public string State { get; set; } = "Draft";
    public byte[] RowVersion { get; set; } = [];
}

public sealed class SupplierBillRevision
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public Guid BillId { get; set; }
    public long Sequence { get; set; }
    public int PurchaseOrderRevision { get; set; }
    public string SupplierName { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public string? NormalizedReference { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class SupplierBillReceipt
{
    public Guid TenantId { get; set; }
    public string Operation { get; set; } = "";
    public Guid RequestId { get; set; }
    public Guid BillId { get; set; }
    public Guid ActorId { get; set; }
    public string CanonicalInput { get; set; } = "{}";
    public string ResultJson { get; set; } = "{}";
    public DateTimeOffset RecordedAtUtc { get; set; }
}
