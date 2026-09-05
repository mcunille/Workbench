// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Identity;

public sealed record VerifiedIdentity(
    string Scheme,
    string Subject,
    Guid UserId,
    Guid TenantId,
    long SecurityVersion);

public interface IIdentityVerifier
{
    Task<VerifiedIdentity?> VerifyAsync(
        string email,
        string credential,
        CancellationToken cancellationToken);
}
