// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Workbench.Server.ServiceAdministration;

public sealed class ServiceAdminAuthenticationEvents(ServiceAdminSessionService sessions, TimeProvider timeProvider) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var token = context.Principal?.FindFirst(ServiceAdminCookieHandler.SessionTokenClaimType)?.Value;
        if (string.IsNullOrWhiteSpace(token) || context.Principal?.FindFirst(ServiceAdminCookieHandler.FormatVersionClaimType)?.Value != ServiceAdminCookieHandler.CurrentFormatVersion)
        { context.RejectPrincipal(); return; }
        var session = await sessions.ResolveAsync(token, timeProvider.GetUtcNow(), context.HttpContext.RequestAborted);
        if (session is null) { context.RejectPrincipal(); return; }
        context.ReplacePrincipal(ServiceAdminCookieHandler.CreateAuthoritativePrincipal(session));
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; }
    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; }
}
