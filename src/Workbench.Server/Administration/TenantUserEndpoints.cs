// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Authorization;
using Workbench.Server.Http;
using Workbench.Server.Identity;
using Workbench.Server.Persistence;
using Workbench.Server.Security;

namespace Workbench.Server.Administration;

public sealed record TenantUserResponse(Guid Id, string? Email, AccountState State);

public sealed record TenantInvitationRequest(string Email);

public static class TenantUserEndpoints
{
    public static IEndpointRouteBuilder MapTenantUserAdministration(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tenant/users")
            .WithTags("Tenant users")
            .RequireAuthorization(WorkbenchPermissions.TenantUsersManage);
        group.MapGet(string.Empty, GetUsersAsync).Produces<IReadOnlyList<TenantUserResponse>>();
        group.MapDelete("/{userId:guid}", DisableAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        group.MapPost("/{userId:guid}/reactivate", ReactivateAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        group.MapPost("/{userId:guid}/recovery", InitiateRecoveryAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapDelete("/{userId:guid}/sessions", RevokeSessionsAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/invitations", InviteAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest);
        return endpoints;
    }

    private static async Task<IResult> GetUsersAsync(
        WorkbenchDbContext database,
        CancellationToken cancellationToken)
    {
        var users = await database.Users
            .OrderBy(user => user.Email)
            .Select(user => new TenantUserResponse(user.Id, user.Email, user.State))
            .ToArrayAsync(cancellationToken);
        return TypedResults.Ok(users);
    }

    private static Task<IResult> DisableAsync(
        Guid userId,
        RequestActor actor,
        WorkbenchDbContext database,
        SecurityAuditWriter audit,
        HttpContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        SetStateAsync(
            userId,
            AccountState.Disabled,
            AccountState.Enabled,
            "tenant.user.disabled",
            actor,
            database,
            audit,
            context,
            timeProvider,
            cancellationToken);

    private static Task<IResult> ReactivateAsync(
        Guid userId,
        RequestActor actor,
        WorkbenchDbContext database,
        SecurityAuditWriter audit,
        HttpContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        SetStateAsync(
            userId,
            AccountState.Enabled,
            AccountState.Disabled,
            "tenant.user.reactivated",
            actor,
            database,
            audit,
            context,
            timeProvider,
            cancellationToken);

    private static async Task<IResult> SetStateAsync(
        Guid userId,
        AccountState state,
        AccountState requiredCurrentState,
        string action,
        RequestActor actor,
        WorkbenchDbContext database,
        SecurityAuditWriter audit,
        HttpContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var user = await database.Users.SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);
        if (user is null)
        {
            return TypedResults.NotFound();
        }

        if (user.State != requiredCurrentState ||
            (state == AccountState.Enabled && user.PasswordHash is null))
        {
            return ApiProblemResults.InvalidRequest("The requested account state transition is not allowed.");
        }

        if (state == AccountState.Disabled)
        {
            if (userId == actor.UserId)
            {
                return ApiProblemResults.InvalidRequest("The current administrator cannot be disabled.");
            }

            var targetIsAdministrator = await (
                from membership in database.Set<WorkbenchUserRole>()
                join claim in database.Set<WorkbenchRoleClaim>()
                    on new { membership.TenantId, membership.RoleId }
                    equals new { claim.TenantId, claim.RoleId }
                where membership.UserId == userId &&
                    claim.ClaimType == SessionCookieHandler.PermissionClaimType &&
                    claim.ClaimValue == WorkbenchPermissions.TenantUsersManage
                select membership.UserId).AnyAsync(cancellationToken);
            if (targetIsAdministrator)
            {
                var enabledAdministratorCount = await (
                    from administrator in database.Users
                    join membership in database.Set<WorkbenchUserRole>()
                        on new { administrator.TenantId, UserId = administrator.Id }
                        equals new { membership.TenantId, membership.UserId }
                    join claim in database.Set<WorkbenchRoleClaim>()
                        on new { membership.TenantId, membership.RoleId }
                        equals new { claim.TenantId, claim.RoleId }
                    where administrator.State == AccountState.Enabled &&
                        claim.ClaimType == SessionCookieHandler.PermissionClaimType &&
                        claim.ClaimValue == WorkbenchPermissions.TenantUsersManage
                    select administrator.Id).Distinct().CountAsync(cancellationToken);
                if (enabledAdministratorCount <= 1)
                {
                    return ApiProblemResults.InvalidRequest("The last enabled administrator cannot be disabled.");
                }
            }
        }

        user.State = state;
        user.SecurityVersion++;
        var now = timeProvider.GetUtcNow();
        await database.Sessions
            .Where(session => session.UserId == userId && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(session => session.RevokedAtUtc, now)
                .SetProperty(session => session.RevocationReason, "AccountStateChanged"),
                cancellationToken);
        audit.AppendTenant(
            actor.TenantId,
            actor.UserId,
            action,
            "User",
            userId,
            "Succeeded",
            context.TraceIdentifier);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> InitiateRecoveryAsync(
        Guid userId,
        WorkbenchDbContext database,
        IdentityOperationService operations,
        CancellationToken cancellationToken)
    {
        if (!operations.PublicOperationsAvailable)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Account recovery is unavailable.");
        }

        var email = await database.Users
            .Where(user => user.Id == userId)
            .Select(user => user.Email)
            .SingleOrDefaultAsync(cancellationToken);
        if (email is null)
        {
            return TypedResults.NotFound();
        }

        await operations.RequestRecoveryAsync(email, cancellationToken);
        return Results.StatusCode(StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> RevokeSessionsAsync(
        Guid userId,
        RequestActor actor,
        WorkbenchDbContext database,
        SecurityAuditWriter audit,
        HttpContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!await database.Users.AnyAsync(user => user.Id == userId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        await database.Sessions
            .Where(session => session.UserId == userId && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(session => session.RevokedAtUtc, now)
                .SetProperty(session => session.RevocationReason, "TenantAdministratorRevoked"),
                cancellationToken);
        audit.AppendTenant(
            actor.TenantId,
            actor.UserId,
            "tenant.user.sessions-revoked",
            "User",
            userId,
            "Succeeded",
            context.TraceIdentifier);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> InviteAsync(
        TenantInvitationRequest request,
        RequestActor actor,
        IdentityOperationService operations,
        CancellationToken cancellationToken)
    {
        var created = await operations.RequestInvitationAsync(
            actor.TenantId,
            request.Email,
            cancellationToken);
        return created
            ? Results.StatusCode(StatusCodes.Status202Accepted)
            : ApiProblemResults.InvalidRequest("The invitation could not be created.");
    }
}
