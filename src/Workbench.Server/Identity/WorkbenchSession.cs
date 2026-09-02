// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Tenancy;

namespace Workbench.Server.Identity;

public sealed class WorkbenchSession : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public required byte[] TokenHash { get; set; }

    public long SecurityVersion { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset LastSeenAtUtc { get; set; }

    public DateTimeOffset IdleExpiresAtUtc { get; set; }

    public DateTimeOffset AbsoluteExpiresAtUtc { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }

    public string? RevocationReason { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
