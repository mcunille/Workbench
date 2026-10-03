// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Gemology;

public sealed class GemReferenceTenantReadMetadata;

public static class GemReferenceEndpoints
{
    public static IEndpointRouteBuilder MapGemReference(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/beta/gem-reference").WithTags("Gem reference").RequireAuthorization()
            .WithMetadata(new GemReferenceTenantReadMetadata());

        group.MapGet("", BrowseAsync).Produces<GemReferencePageResponse>().ProducesProblem(400);
        group.MapGet("/{id:guid}", DetailAsync).Produces<GemReferenceDetailResponse>().ProducesProblem(400).ProducesProblem(404);
        return endpoints;
    }
    private static IResult Problem(int status, string code, string title) => Results.Problem(
        statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
    private static async Task<IResult> BrowseAsync(string? query, string? materialKind, string? group,
        string? cursor, bool? includeArchived, GemReferenceEffectiveReadService reads, CancellationToken cancellationToken)
    {
        if (query?.Length > 200 || query?.Any(char.IsControl) == true)
            return Problem(400, "invalid_query", "Use up to 200 characters without control characters for search.");
        if (group?.Length > 200 || group?.Any(char.IsControl) == true ||
            (materialKind is not null && !GemReferenceInput.MaterialKinds.Contains(materialKind)))
            return Problem(400, "invalid_filter", "Review the material kind and group filters.");
        query = string.IsNullOrWhiteSpace(query) ? null : query.Trim(); group = string.IsNullOrWhiteSpace(group) ? null : group.Trim();
        var search = new GemReferenceSearch(query, materialKind, group);
        if (!GemReferenceCursor.TryDecodeEffective(cursor, search, includeArchived ?? false, out var position))
            return Problem(400, "invalid_cursor", "Refresh the gem reference to start a new page.");
        return Results.Ok(await reads.BrowseAsync(search, position, includeArchived ?? false, cancellationToken));
    }
    private static async Task<IResult> DetailAsync(Guid id, string? origin, GemReferenceEffectiveReadService reads, CancellationToken cancellationToken)
    {
        if (origin is not (null or "tenant" or "workbench")) return Problem(400, "invalid_origin", "Select tenant or workbench reference origin.");
        return await reads.DetailAsync(id, origin, cancellationToken) is { } response ? Results.Ok(response) :
            Problem(404, "gem_reference_not_found", "Gem reference not found.");
    }
}
