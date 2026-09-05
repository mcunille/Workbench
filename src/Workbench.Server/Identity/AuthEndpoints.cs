// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Authorization;
using Workbench.Server.Http;
using Workbench.Server.Persistence;
using Workbench.Server.Security;

namespace Workbench.Server.Identity;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapWorkbenchAuthentication(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth").WithTags("Authentication");

        group.MapGet("/antiforgery", (IAntiforgery antiforgery, HttpContext context) =>
            {
                var tokens = antiforgery.GetAndStoreTokens(context);
                return TypedResults.Ok(new AntiforgeryResponse(
                    tokens.RequestToken ?? throw new InvalidOperationException("Antiforgery token generation failed.")));
            })
            .AllowAnonymous()
            .Produces<AntiforgeryResponse>();

        group.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        var authenticated = group.MapGroup(string.Empty).RequireAuthorization();
        authenticated.MapGet("/me", GetCurrentIdentityAsync)
            .Produces<CurrentIdentityResponse>()
            .Produces(StatusCodes.Status401Unauthorized);
        authenticated.MapPost("/logout", LogoutAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces(StatusCodes.Status204NoContent);
        authenticated.MapPost("/change-password", ChangePasswordAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        authenticated.MapGet("/sessions", GetSessionsAsync)
            .Produces<IReadOnlyList<SessionResponse>>();
        authenticated.MapDelete("/sessions/{sessionId:guid}", RevokeSessionAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces(StatusCodes.Status204NoContent);
        authenticated.MapDelete("/sessions", RevokeAllSessionsAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces(StatusCodes.Status204NoContent);

        return endpoints;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        IIdentityVerifier verifier,
        SessionService sessions,
        ISensitiveRequestRateLimiter rateLimiter,
        SessionOptions sessionOptions,
        TimeProvider timeProvider,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) ||
            request.Email.Length > 256 ||
            string.IsNullOrEmpty(request.Password) ||
            request.Password.Length > 1024)
        {
            return ApiProblemResults.AuthenticationFailed();
        }

        var networkAllowed = await rateLimiter.TryAcquireAsync(
            SensitiveRequestPartitions.LoginNetwork(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
            cancellationToken);
        if (!networkAllowed)
        {
            return ApiProblemResults.AuthenticationFailed();
        }

        var accountAllowed = await rateLimiter.TryAcquireAsync(
            SensitiveRequestPartitions.LoginAccount(request.Email),
            cancellationToken);
        if (!accountAllowed)
        {
            return ApiProblemResults.AuthenticationFailed();
        }

        var verified = await verifier.VerifyAsync(request.Email, request.Password, cancellationToken);
        if (verified is null)
        {
            return ApiProblemResults.AuthenticationFailed();
        }

        CreatedSession session;
        try
        {
            session = await sessions.CreateAsync(verified, timeProvider.GetUtcNow(), cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return ApiProblemResults.AuthenticationFailed();
        }

        await context.SignInAsync(
            SessionCookieHandler.Scheme,
            SessionCookieHandler.CreateCookiePrincipal(session.Token),
            SessionCookieHandler.CreateCookieProperties(session, sessionOptions));
        return TypedResults.NoContent();
    }

    private static async Task<IResult> GetCurrentIdentityAsync(
        RequestActor actor,
        WorkbenchDbContext database,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var tenantName = await database.Tenants
            .Where(tenant => tenant.Id == actor.TenantId)
            .Select(tenant => tenant.Name)
            .SingleAsync(cancellationToken);
        var permissions = actor.Permissions.Order(StringComparer.Ordinal).ToArray();
        return TypedResults.Ok(new CurrentIdentityResponse(
            actor.UserId,
            principal.FindFirstValue(ClaimTypes.Email),
            tenantName,
            permissions));
    }

    private static async Task<IResult> LogoutAsync(
        RequestActor actor,
        SessionService sessions,
        TimeProvider timeProvider,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        await sessions.RevokeAsync(
            actor.TenantId,
            actor.SessionId,
            "UserSignOut",
            timeProvider.GetUtcNow(),
            cancellationToken);
        await context.SignOutAsync(SessionCookieHandler.Scheme);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        RequestActor actor,
        WorkbenchDbContext database,
        UserManager<WorkbenchUser> userManager,
        SecurityAuditWriter audit,
        TimeProvider timeProvider,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!WorkbenchPasswordPolicy.IsWithinInputBounds(request.CurrentPassword) ||
            !WorkbenchPasswordPolicy.IsWithinInputBounds(request.NewPassword))
        {
            return ApiProblemResults.InvalidRequest("The password input is invalid.");
        }

        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var user = await database.Users
            .FromSqlInterpolated($"""
                SELECT * FROM [Identity].[Users] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = {actor.UserId} AND [TenantId] = {actor.TenantId}
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null || user.PasswordHash is null ||
            userManager.PasswordHasher.VerifyHashedPassword(
                user,
                user.PasswordHash,
                request.CurrentPassword) is PasswordVerificationResult.Failed)
        {
            return ApiProblemResults.InvalidRequest("The current password is incorrect.");
        }

        foreach (var validator in userManager.PasswordValidators)
        {
            var result = await validator.ValidateAsync(userManager, user, request.NewPassword);
            if (!result.Succeeded)
            {
                var description = string.Join(" ", result.Errors.Select(error => error.Description));
                return ApiProblemResults.InvalidRequest(description);
            }
        }

        var now = timeProvider.GetUtcNow();
        user.PasswordHash = userManager.PasswordHasher.HashPassword(user, request.NewPassword);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        user.SecurityVersion++;
        await database.Sessions
            .Where(session => session.UserId == actor.UserId && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(session => session.RevokedAtUtc, now)
                .SetProperty(session => session.RevocationReason, "PasswordChanged"),
                cancellationToken);
        audit.AppendTenant(
            actor.TenantId,
            actor.UserId,
            "identity.password.changed",
            "User",
            actor.UserId,
            "Succeeded",
            context.TraceIdentifier);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await context.SignOutAsync(SessionCookieHandler.Scheme);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> GetSessionsAsync(
        RequestActor actor,
        WorkbenchDbContext database,
        CancellationToken cancellationToken)
    {
        var sessions = await database.Sessions
            .Where(session => session.UserId == actor.UserId && session.RevokedAtUtc == null)
            .OrderByDescending(session => session.LastSeenAtUtc)
            .Select(session => new SessionResponse(
                session.Id,
                session.CreatedAtUtc,
                session.LastSeenAtUtc,
                session.IdleExpiresAtUtc,
                session.AbsoluteExpiresAtUtc,
                session.Id == actor.SessionId))
            .ToArrayAsync(cancellationToken);
        return TypedResults.Ok(sessions);
    }

    private static async Task<IResult> RevokeSessionAsync(
        Guid sessionId,
        RequestActor actor,
        SessionService sessions,
        TimeProvider timeProvider,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        await sessions.RevokeUserSessionAsync(
            actor.TenantId,
            actor.UserId,
            sessionId,
            "UserRevoked",
            timeProvider.GetUtcNow(),
            cancellationToken);
        if (sessionId == actor.SessionId)
        {
            await context.SignOutAsync(SessionCookieHandler.Scheme);
        }

        return TypedResults.NoContent();
    }

    private static async Task<IResult> RevokeAllSessionsAsync(
        RequestActor actor,
        SessionService sessions,
        TimeProvider timeProvider,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        await sessions.RevokeAllAsync(
            actor.TenantId,
            actor.UserId,
            "UserRevokedAll",
            timeProvider.GetUtcNow(),
            cancellationToken);
        await context.SignOutAsync(SessionCookieHandler.Scheme);
        return TypedResults.NoContent();
    }
}
