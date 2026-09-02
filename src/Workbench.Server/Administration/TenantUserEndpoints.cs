// Copyright (c) 2026 The White Stag Collection.

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
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/{userId:guid}/reactivate", ReactivateAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/{userId:guid}/recovery", InitiateRecoveryAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces(StatusCodes.Status202Accepted)
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
        string action,
        RequestActor actor,
        WorkbenchDbContext database,
        SecurityAuditWriter audit,
        HttpContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var user = await database.Users.SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);
        if (user is null)
        {
            return TypedResults.NotFound();
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
