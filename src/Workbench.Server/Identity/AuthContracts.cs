// Copyright (c) 2026 The White Stag Collection.

using System.Text.Json.Serialization;

namespace Workbench.Server.Identity;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LoginRequest(string Email, string Password);

public sealed record AntiforgeryResponse(string RequestToken);

public sealed record CurrentIdentityResponse(
    Guid UserId,
    string? Email,
    string TenantName,
    IReadOnlyCollection<string> Permissions);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record SessionResponse(
    Guid Id,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastSeenAtUtc,
    DateTimeOffset IdleExpiresAtUtc,
    DateTimeOffset AbsoluteExpiresAtUtc,
    bool IsCurrent);
