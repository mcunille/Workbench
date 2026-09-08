// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Tenancy;

namespace Workbench.Server.Inventory;

public sealed class InventoryItem : ITenantOwned
{
    public Guid Id { get; init; }
    public Guid TenantId { get; set; }
    public string TrackingKind { get; init; } = "Individual";
    public required string Name { get; init; }
    public string? Notes { get; init; }
    public string? StorageLocation { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? ArchivedAtUtc { get; init; }
    public Guid CreationRequestId { get; init; }
    public Guid? CurrentPhotoId { get; set; }
    public ItemPhoto? CurrentPhoto { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
