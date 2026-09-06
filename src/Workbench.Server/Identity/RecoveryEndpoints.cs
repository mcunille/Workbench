// Copyright (c) 2026 The White Stag Collection.

using System.Text.Json.Serialization;
using Workbench.Server.Http;

namespace Workbench.Server.Identity;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecoveryRequest(string Email);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecoveryConsumeRequest(string Token, string NewPassword);

public static class RecoveryEndpoints
{
    public static IEndpointRouteBuilder MapWorkbenchRecovery(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/auth/recovery", RequestAsync)
            .AllowAnonymous()
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        endpoints.MapPost("/api/auth/recovery/consume", ConsumeAsync)
            .AllowAnonymous()
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        endpoints.MapPost("/api/auth/invitations/consume", ConsumeInvitationAsync)
            .AllowAnonymous()
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return endpoints;
    }

    private static async Task<IResult> RequestAsync(
        RecoveryRequest request,
        IdentityOperationService operations,
        CancellationToken cancellationToken)
    {
        if (!operations.PublicOperationsAvailable)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Public account recovery is unavailable.");
        }

        if (!string.IsNullOrWhiteSpace(request.Email) && request.Email.Length <= 256)
        {
            await operations.RequestRecoveryAsync(request.Email, cancellationToken);
        }

        return Results.StatusCode(StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> ConsumeAsync(
        RecoveryConsumeRequest request,
        IdentityOperationService operations,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!operations.PublicOperationsAvailable)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Public account recovery is unavailable.");
        }

        if (!HasBoundedConsumeInput(request))
        {
            return ApiProblemResults.InvalidRequest("The recovery operation is invalid or expired.");
        }

        var consumed = await operations.ConsumeRecoveryAsync(
            request.Token,
            request.NewPassword,
            context.TraceIdentifier,
            cancellationToken);
        return consumed
            ? TypedResults.NoContent()
            : ApiProblemResults.InvalidRequest("The recovery operation is invalid or expired.");
    }

    private static async Task<IResult> ConsumeInvitationAsync(
        RecoveryConsumeRequest request,
        IdentityOperationService operations,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!operations.PublicInvitationsAvailable)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Public invitations are unavailable.");
        }

        if (!HasBoundedConsumeInput(request))
        {
            return ApiProblemResults.InvalidRequest("The invitation is invalid or expired.");
        }

        var consumed = await operations.ConsumeInvitationAsync(
            request.Token,
            request.NewPassword,
            context.TraceIdentifier,
            cancellationToken);
        return consumed
            ? TypedResults.NoContent()
            : ApiProblemResults.InvalidRequest("The invitation is invalid or expired.");
    }

    private static bool HasBoundedConsumeInput(RecoveryConsumeRequest request) =>
        request.Token is { Length: SessionToken.EncodedLength } &&
        WorkbenchPasswordPolicy.IsWithinInputBounds(request.NewPassword);
}
