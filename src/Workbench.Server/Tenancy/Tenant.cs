// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Tenancy;

public sealed class Tenant
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public required string NormalizedName { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
