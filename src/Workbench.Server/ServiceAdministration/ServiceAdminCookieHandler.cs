// Copyright (c) 2026 The White Stag Collection.

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Workbench.Server.Identity;
using SessionOptions = Workbench.Server.Identity.SessionOptions;

namespace Workbench.Server.ServiceAdministration;

public static class ServiceAdminCookieHandler
{
    public const string Scheme = "WorkbenchServiceAdmin";
    public const string Policy = "ServiceAdmin";
    public const string SessionTokenClaimType = "workbench/service_admin/session_token";
    public const string FormatVersionClaimType = "workbench/service_admin/format_version";
    public const string AccountIdClaimType = "workbench/service_admin/account_id";
    public const string SessionIdClaimType = "workbench/service_admin/session_id";
    public const string CurrentFormatVersion = "1";

    public static bool IsServiceAdminRequest(HttpContext? context) =>
        context?.Request.Path.StartsWithSegments("/api/beta/service-admin", StringComparison.OrdinalIgnoreCase) == true;

    public static ClaimsPrincipal CreateCookiePrincipal(string token) => new(new ClaimsIdentity(
        [new Claim(SessionTokenClaimType, token), new Claim(FormatVersionClaimType, CurrentFormatVersion)], Scheme));

    public static AuthenticationProperties CreateCookieProperties(CreatedSession session, SessionOptions options) => new()
    {
        AllowRefresh = false,
        IsPersistent = true,
        IssuedUtc = session.IdleExpiresAtUtc - options.IdleTimeout,
        ExpiresUtc = session.AbsoluteExpiresAtUtc,
    };

    public static ClaimsPrincipal CreateAuthoritativePrincipal(ResolvedServiceAdminSession session) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "service-admin:" + session.AccountId.ToString("N")), new Claim(AccountIdClaimType, session.AccountId.ToString("N")), new Claim(SessionIdClaimType, session.SessionId.ToString("N")),
            new Claim(ClaimTypes.Email, session.Email), new Claim(ClaimTypes.Name, session.Email)], Scheme));
}
