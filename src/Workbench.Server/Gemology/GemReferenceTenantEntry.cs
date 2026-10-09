// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Tenancy;

namespace Workbench.Server.Gemology;

public sealed class GemReferenceTenantEntry : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid Id { get; init; }
    public required string ContentJson { get; set; }
    public bool IsArchived { get; set; }
    public Guid CreatedBy { get; init; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class GemReferenceTenantOverride : ITenantOwned
{
    public Guid TenantId { get; set; }
    public Guid EntryId { get; init; }
    public required string OverridesJson { get; set; }
    public Guid CreatedBy { get; init; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
