// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Tenancy;

namespace Workbench.Server.Inventory;

// Captured once by the first checked edit, before the creation payload can change.
public sealed class ItemCreationSnapshot : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid ItemId { get; init; }
    public required string Name { get; init; }
    public string? Notes { get; init; }
    public string? StorageLocation { get; init; }
}
