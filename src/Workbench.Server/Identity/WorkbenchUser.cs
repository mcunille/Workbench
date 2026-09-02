// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Identity;
using Workbench.Server.Tenancy;

namespace Workbench.Server.Identity;

public enum AccountState
{
    Enabled = 1,
    Disabled = 2,
}

public sealed class WorkbenchUser : IdentityUser<Guid>, ITenantOwned
{
    public Guid TenantId { get; set; }

    public long SecurityVersion { get; set; } = 1;

    public AccountState State { get; set; } = AccountState.Enabled;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
