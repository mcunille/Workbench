// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Tenancy;

namespace Workbench.Server.Inventory;

public sealed class Acquisition : ITenantOwned
{
    public Guid Id { get; init; }
    public Guid TenantId { get; set; }
    public required string Method { get; set; }
    public string? Source { get; set; }
    public int? Year { get; set; }
    public int? Month { get; set; }
    public int? Day { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public Guid CreationRequestId { get; init; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class AcquisitionItem : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid ItemId { get; init; }
    public Guid AcquisitionId { get; init; }
    public Acquisition Acquisition { get; init; } = null!;
}

public sealed class AcquisitionCreationRecord : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid CreationRequestId { get; init; }
    public Guid AcquisitionId { get; init; }
    public Guid ItemId { get; init; }
    public required string Method { get; init; }
    public string? Source { get; init; }
    public int? Year { get; init; }
    public int? Month { get; init; }
    public int? Day { get; init; }
    public string? Notes { get; init; }
}
