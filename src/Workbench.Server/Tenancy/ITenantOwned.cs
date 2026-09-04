// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Tenancy;

public interface ITenantOwned
{
    Guid TenantId { get; set; }
}
