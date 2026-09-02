// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Authorization;

public sealed record RequestActor(
    Guid UserId,
    Guid TenantId,
    Guid SessionId,
    IReadOnlySet<string> Permissions);
