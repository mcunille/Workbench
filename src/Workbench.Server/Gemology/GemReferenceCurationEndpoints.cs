// Copyright (c) 2026 The White Stag Collection.

using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Workbench.Server.Http;
using Workbench.Server.ServiceAdministration;

namespace Workbench.Server.Gemology;

public static class GemReferenceCurationEndpoints
{
    public static IEndpointRouteBuilder MapGemReferenceCuration(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/beta/service-admin/gem-reference").WithTags("Shared gem curation")
            .RequireAuthorization(ServiceAdminCookieHandler.Policy);
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            try { return await next(context); }
            catch (SqlException error) when (error.Number is 50041 or 50042 or 50043 or 50044)
            {
                return Problem(error.Number switch { 50041 => 401, 50042 or 50044 => 409, _ => 400 },
                    error.Number switch { 50041 => "admin_authority_required", 50042 => "catalog_busy", 50044 => "stale_entry", _ => "invalid_request" },
                    "Reload and review the shared catalog request.");
            }
        });
        group.MapGet("", BrowseAsync).Produces<GemReferencePageResponse>().ProducesProblem(400);
        group.MapGet("/{id:guid}", async (Guid id, GemReferenceAdminReadService reads, CancellationToken ct) =>
            await reads.DetailAsync(id, ct) is { } entry ? Results.Ok(entry) : Problem(404, "gem_reference_not_found", "Shared entry not found."))
            .Produces<GemReferenceDetailResponse>().ProducesProblem(404);
        group.MapGet("/drafts", async (string? cursor, ClaimsPrincipal user, GemReferenceDraftService drafts, CancellationToken ct) =>
            Cursor(cursor, out var after) ? Results.Ok(await drafts.ListAsync(Account(user), Session(user), after, ct)) : Invalid())
            .Produces<GemReferenceDraftPage>().ProducesProblem(400);
        group.MapGet("/drafts/{draftId:guid}", async (Guid draftId, ClaimsPrincipal user, GemReferenceDraftService drafts, CancellationToken ct) =>
            await drafts.ReadAsync(Account(user), Session(user), draftId, ct) is { } draft ? Results.Ok(draft) : Problem(404, "draft_not_found", "Draft not found."))
            .Produces<GemReferenceDraftResponse>().ProducesProblem(404);
        Write(group.MapPut("/drafts/{draftId:guid}", SaveAsync)).Produces<GemReferenceDraftResponse>().ProducesProblem(400).ProducesProblem(409);
        Write(group.MapPost("/review", async (GemReferenceReviewRequest request, ClaimsPrincipal user, GemReferencePublicationService publisher, CancellationToken ct) =>
            Selection(request.Drafts) ? Results.Ok(await publisher.ReviewAsync(Account(user), Session(user), request.Drafts, ct)) : Invalid()))
            .Produces<GemReferenceReviewResponse>().ProducesProblem(400);
        Write(group.MapPost("/publish", PublishAsync)).Produces<GemReferencePublishOutcome>().Produces<GemReferencePublishOutcome>(409)
            .Produces<GemReferencePublishOutcome>(422).ProducesProblem(400);
        group.MapGet("/publications/{requestId:guid}", async (Guid requestId, ClaimsPrincipal user, GemReferencePublicationService publisher, CancellationToken ct) =>
            await publisher.ReadAsync(Account(user), Session(user), requestId, ct) is { } outcome ? Results.Ok(outcome) : Problem(404, "publication_not_found", "Publication not found."))
            .Produces<GemReferencePublishOutcome>().ProducesProblem(404);
        group.MapGet("/publication-audit", async (string? cursor, ClaimsPrincipal user, GemReferencePublicationService publisher, CancellationToken ct) =>
            Cursor(cursor, out var after) ? Results.Ok(await publisher.AuditAsync(Account(user), Session(user), after, ct)) : Invalid())
            .Produces<GemReferencePublicationAuditPage>().ProducesProblem(400);
        return endpoints;
    }

    private static RouteHandlerBuilder Write(RouteHandlerBuilder route) => route.WithMetadata(WorkbenchAntiforgeryMetadata.Instance, new GemReferenceCurationWriteMetadata());
    private static Guid Account(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ServiceAdminCookieHandler.AccountIdClaimType)!);
    private static Guid Session(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ServiceAdminCookieHandler.SessionIdClaimType)!);
    private static IResult Problem(int status, string code, string title) => Results.Problem(statusCode: status, title: title,
        extensions: new Dictionary<string, object?> { ["code"] = code });
    private static IResult Invalid() => Problem(400, "invalid_request", "Review the shared catalog request fields and concurrency tokens.");
    private static bool Cursor(string? value, out Guid? after)
    {
        after = null;
        if (value is null) return true;
        if (!Guid.TryParseExact(value, "N", out var id) || id == Guid.Empty) return false;
        after = id;
        return true;
    }
    private static bool Version(string? value, bool optional = false)
    {
        if (value is null) return optional;
        if (value.Length != 12) return false;
        Span<byte> bytes = stackalloc byte[8];
        return Convert.TryFromBase64String(value, bytes, out var count) && count == 8;
    }
    private static bool Selection(IReadOnlyList<GemReferenceDraftSelection>? drafts) => drafts is { Count: >= 1 and <= 50 }
        && drafts.All(d => d is not null && d.DraftId != Guid.Empty && Version(d.ExpectedDraftRowVersion))
        && drafts.Select(d => d.DraftId).Distinct().Count() == drafts.Count;

    private static async Task<IResult> SaveAsync(Guid draftId, GemReferenceDraftSaveRequest request, ClaimsPrincipal user,
        GemReferenceDraftService drafts, CancellationToken ct)
    {
        if (draftId == Guid.Empty || request.EntryId == Guid.Empty || request.Content is null || request.Content.Id != request.EntryId
            || request.Content.Aliases is null || request.Content.Sources is null || request.Content.Sources.Any(s => s is null)
            || !Version(request.ExpectedDraftRowVersion, true) || !Version(request.ExpectedPublishedRowVersion, true)) return Invalid();
        return Results.Ok(await drafts.SaveAsync(Account(user), Session(user), draftId, request, ct));
    }
    private static async Task<IResult> PublishAsync(GemReferencePublishRequest request, ClaimsPrincipal user,
        GemReferencePublicationService publisher, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty || !Selection(request.Drafts)) return Invalid();
        var outcome = await publisher.PublishAsync(Account(user), Session(user), request, ct);
        return Results.Json(outcome, statusCode: outcome.Code switch
        {
            "published" => 200,
            "validation_failed" => outcome.Review.Any(entry => entry.Errors.ContainsKey("identity")) ? 409 : 422,
            _ => 409
        });
    }
    private static async Task<IResult> BrowseAsync(string? query, string? materialKind, string? group, string? cursor,
        GemReferenceAdminReadService reads, CancellationToken ct)
    {
        if (query?.Length > 200 || query?.Any(char.IsControl) == true) return Problem(400, "invalid_query", "Review the shared catalog search.");
        if (group?.Length > 200 || group?.Any(char.IsControl) == true || (materialKind is not null && !GemReferenceInput.MaterialKinds.Contains(materialKind)))
            return Problem(400, "invalid_filter", "Review the shared catalog filters.");
        try { return Results.Ok(await reads.BrowseAsync(query, materialKind, group, cursor, ct)); }
        catch (ArgumentException) { return Problem(400, "invalid_cursor", "Refresh the shared catalog search."); }
    }
}
