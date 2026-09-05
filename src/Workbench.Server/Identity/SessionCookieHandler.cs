// Copyright (c) 2026 The White Stag Collection.

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Workbench.Server.Identity;

public static class SessionCookieHandler
{
    public const string Scheme = "WorkbenchSession";
    public const string SessionTokenClaimType = "workbench/session_token";
    public const string FormatVersionClaimType = "workbench/format_version";
    public const string TenantIdClaimType = "workbench/tenant_id";
    public const string SessionIdClaimType = "workbench/session_id";
    public const string PermissionClaimType = "workbench/permission";
    public const string CurrentFormatVersion = "1";

    public static ClaimsPrincipal CreateCookiePrincipal(string sessionToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);
        var identity = new ClaimsIdentity(
            [
                new Claim(SessionTokenClaimType, sessionToken),
                new Claim(FormatVersionClaimType, CurrentFormatVersion),
            ],
            Scheme);
        return new ClaimsPrincipal(identity);
    }

    public static AuthenticationProperties CreateCookieProperties(
        CreatedSession session,
        SessionOptions options) => new()
        {
            AllowRefresh = false,
            IsPersistent = true,
            IssuedUtc = session.IdleExpiresAtUtc - options.IdleTimeout,
            ExpiresUtc = session.AbsoluteExpiresAtUtc,
        };

    public static ClaimsPrincipal CreateAuthoritativePrincipal(ResolvedSession session)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, session.UserId.ToString("N")),
            new(SessionIdClaimType, session.SessionId.ToString("N")),
            new(TenantIdClaimType, session.TenantId.ToString("N")),
        };
        if (!string.IsNullOrWhiteSpace(session.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, session.Email));
            claims.Add(new Claim(ClaimTypes.Name, session.Email));
        }

        claims.AddRange(session.Permissions.Select(permission => new Claim(PermissionClaimType, permission)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme, ClaimTypes.Name, PermissionClaimType));
    }
}
