// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Identity;
using Workbench.Server.Tenancy;

namespace Workbench.Server.Identity;

public sealed class WorkbenchRole : IdentityRole<Guid>, ITenantOwned
{
    public Guid TenantId { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
