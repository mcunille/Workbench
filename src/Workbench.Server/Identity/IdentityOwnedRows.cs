// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Identity;
using Workbench.Server.Tenancy;

namespace Workbench.Server.Identity;

public sealed class WorkbenchUserClaim : IdentityUserClaim<Guid>, ITenantOwned
{
    public Guid TenantId { get; set; }
}

public sealed class WorkbenchUserRole : IdentityUserRole<Guid>, ITenantOwned
{
    public Guid TenantId { get; set; }
}

public sealed class WorkbenchUserLogin : IdentityUserLogin<Guid>, ITenantOwned
{
    public Guid TenantId { get; set; }
}

public sealed class WorkbenchRoleClaim : IdentityRoleClaim<Guid>, ITenantOwned
{
    public Guid TenantId { get; set; }
}

public sealed class WorkbenchUserToken : IdentityUserToken<Guid>, ITenantOwned
{
    public Guid TenantId { get; set; }
}
