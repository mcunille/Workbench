// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Tenancy;

public sealed record TenantContext(Guid? TenantId)
{
    public static TenantContext None { get; } = new((Guid?)null);

    public Guid RequireTenantId() =>
        TenantId ?? throw new InvalidOperationException("Tenant context is required.");
}
