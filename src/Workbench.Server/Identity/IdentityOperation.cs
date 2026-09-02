// Copyright (c) 2026 The White Stag Collection.

using Workbench.Server.Tenancy;

namespace Workbench.Server.Identity;

public enum IdentityOperationPurpose
{
    PasswordRecovery = 1,
    Invitation = 2,
}

public sealed class IdentityOperation : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public IdentityOperationPurpose Purpose { get; set; }

    public required byte[] TokenHash { get; set; }

    public long SecurityVersion { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset ExpiresAtUtc { get; set; }

    public DateTimeOffset? ConsumedAtUtc { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
