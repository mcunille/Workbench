// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Identity;

public sealed class LoginDirectoryEntry
{
    public required string NormalizedEmail { get; set; }

    public Guid UserId { get; set; }

    public Guid TenantId { get; set; }
}
