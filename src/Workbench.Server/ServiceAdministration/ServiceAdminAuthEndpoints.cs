// Copyright (c) 2026 The White Stag Collection.

using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Workbench.Server.Http;
using Workbench.Server.Identity;
using SessionOptions = Workbench.Server.Identity.SessionOptions;

namespace Workbench.Server.ServiceAdministration;

public static class ServiceAdminAuthEndpoints
{
    public static IEndpointRouteBuilder MapServiceAdminAuthentication(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/beta/service-admin/auth").WithTags("Service-admin Authentication");
        group.MapGet("/antiforgery", (IAntiforgery antiforgery, HttpContext context) =>
            TypedResults.Ok(new AntiforgeryResponse(antiforgery.GetAndStoreTokens(context).RequestToken
                ?? throw new InvalidOperationException("Antiforgery token generation failed."))))
            .AllowAnonymous().Produces<AntiforgeryResponse>();
        group.MapPost("/login", LoginAsync).AllowAnonymous().WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(400).ProducesProblem(401);
        var authenticated = group.MapGroup("").RequireAuthorization(ServiceAdminCookieHandler.Policy);
        authenticated.MapGet("/me", (ClaimsPrincipal principal) => TypedResults.Ok(new CurrentServiceAdminResponse(
            Guid.Parse(principal.FindFirstValue(ServiceAdminCookieHandler.AccountIdClaimType)!), principal.FindFirstValue(ClaimTypes.Email)!)))
            .Produces<CurrentServiceAdminResponse>().Produces(401);
        authenticated.MapPost("/logout", LogoutAsync).WithMetadata(WorkbenchAntiforgeryMetadata.Instance).Produces(204).ProducesProblem(400).Produces(401);
        return endpoints;
    }

    private static async Task<IResult> LoginAsync(ServiceAdminLoginRequest request, ServiceAdminSessionService sessions,
        ISensitiveRequestRateLimiter limiter, SessionOptions options, TimeProvider clock, HttpContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 256 ||
            !WorkbenchPasswordPolicy.IsWithinInputBounds(request.Password)) return ApiProblemResults.AuthenticationFailed();
        if (!await limiter.TryAcquireAsync(SensitiveRequestPartitions.ServiceAdminLoginNetwork(context.Connection.RemoteIpAddress?.ToString() ?? "unknown"), cancellationToken) ||
            !await limiter.TryAcquireAsync(SensitiveRequestPartitions.ServiceAdminLoginAccount(request.Email), cancellationToken)) return ApiProblemResults.AuthenticationFailed();
        var identity = await sessions.VerifyAsync(request.Email, request.Password, cancellationToken);
        if (identity is null) return ApiProblemResults.AuthenticationFailed();
        CreatedSession session;
        try { session = await sessions.CreateAsync(identity, clock.GetUtcNow(), cancellationToken); }
        catch (InvalidOperationException) { return ApiProblemResults.AuthenticationFailed(); }
        await context.SignInAsync(ServiceAdminCookieHandler.Scheme, ServiceAdminCookieHandler.CreateCookiePrincipal(session.Token),
            ServiceAdminCookieHandler.CreateCookieProperties(session, options));
        return TypedResults.NoContent();
    }

    private static async Task<IResult> LogoutAsync(ClaimsPrincipal principal, ServiceAdminSessionService sessions,
        TimeProvider clock, HttpContext context, CancellationToken cancellationToken)
    {
        await sessions.RevokeAsync(Guid.Parse(principal.FindFirstValue(ServiceAdminCookieHandler.AccountIdClaimType)!),
            Guid.Parse(principal.FindFirstValue(ServiceAdminCookieHandler.SessionIdClaimType)!), clock.GetUtcNow(), cancellationToken);
        await context.SignOutAsync(ServiceAdminCookieHandler.Scheme);
        return TypedResults.NoContent();
    }
}
