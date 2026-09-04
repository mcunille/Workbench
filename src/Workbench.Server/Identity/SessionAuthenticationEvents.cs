// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Workbench.Server.Identity;

public sealed class SessionAuthenticationEvents(
    IServiceProvider services,
    TimeProvider timeProvider) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var token = context.Principal?.FindFirst(SessionCookieHandler.SessionTokenClaimType)?.Value;
        var formatVersion = context.Principal?.FindFirst(SessionCookieHandler.FormatVersionClaimType)?.Value;
        if (string.IsNullOrWhiteSpace(token) ||
            formatVersion is not SessionCookieHandler.CurrentFormatVersion)
        {
            context.RejectPrincipal();
            return;
        }

        var sessions = services.GetService<SessionService>();
        if (sessions is null)
        {
            context.RejectPrincipal();
            return;
        }

        var resolved = await sessions.ResolveAsync(
            token,
            timeProvider.GetUtcNow(),
            context.HttpContext.RequestAborted);
        if (resolved is null)
        {
            context.RejectPrincipal();
            return;
        }

        context.ReplacePrincipal(SessionCookieHandler.CreateAuthoritativePrincipal(resolved));
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
