// Copyright (c) 2026 The White Stag Collection.
using Workbench.Server.Tenancy;
namespace Workbench.Server.Purchasing;

public sealed class Supplier : ITenantOwned
{
    public Guid Id { get; init; }
    public Guid TenantId { get; set; }
    public required string Name { get; init; }
    public string? ContactName { get; init; }
    public string? Email { get; init; }
    public string? Phone { get; init; }
    public string? Website { get; init; }
    public string? PostalAddress { get; init; }
    public bool IsArchived { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public Guid CreatedByUserId { get; init; }
    public Guid UpdatedByUserId { get; init; }
    public byte[] RowVersion { get; set; } = [];
}
public sealed class PurchaseOrderCounter
{
    public Guid TenantId { get; init; }
    public long LastNumber { get; init; }
}
public sealed class SupplierRequestReceipt
{
    public Guid TenantId { get; init; }
    public Guid RequestId { get; init; }
    public Guid SupplierId { get; init; }
    public required string Operation { get; init; }
    public Guid ActorUserId { get; init; }
    public byte[]? ExpectedRowVersion { get; init; }
    public required byte[] InputFingerprint { get; init; }
    public required byte[] ResultRowVersion { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
}
