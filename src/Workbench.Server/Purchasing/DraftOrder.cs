// Copyright (c) 2026 The White Stag Collection.
using Workbench.Server.Tenancy;
namespace Workbench.Server.Purchasing;

public sealed class DraftOrder : ITenantOwned
{
    public Guid Id { get; init; }
    public Guid TenantId { get; set; }
    public bool IsDeleted { get; init; }
    public string? Title { get; init; }
    public string? SupplierName { get; init; }
    public long? PoNumber { get; init; }
    public Guid? SupplierId { get; init; }
    public string? SupplierContactName { get; init; }
    public string? SupplierEmail { get; init; }
    public string? SupplierPhone { get; init; }
    public string? SupplierWebsite { get; init; }
    public string? SupplierPostalAddress { get; init; }
    public string? SupplierOrderReference { get; init; }
    public string? Platform { get; init; }
    public string? Currency { get; init; }
    public string? Notes { get; init; }
    public short ContentSchemaVersion { get; init; } = 1;
    public required string ContentJson { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public Guid CreatedByUserId { get; init; }
    public Guid UpdatedByUserId { get; init; }
    public byte[] RowVersion { get; set; } = [];
}
