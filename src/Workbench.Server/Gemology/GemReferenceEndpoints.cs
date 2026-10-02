// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Persistence;

namespace Workbench.Server.Gemology;

public static class GemReferenceEndpoints
{
    private const string Collation = "Latin1_General_100_CI_AS";

    public static IEndpointRouteBuilder MapGemReference(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/beta/gem-reference").WithTags("Gem reference").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            return await next(context);
        });
        group.MapGet("", BrowseAsync).Produces<GemReferencePageResponse>().ProducesProblem(400);
        group.MapGet("/{id:guid}", DetailAsync).Produces<GemReferenceDetailResponse>().ProducesProblem(404);
        return endpoints;
    }

    private static IResult Problem(int status, string code, string title) => Results.Problem(
        statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });

    private static async Task<IResult> BrowseAsync(string? query, string? materialKind, string? group,
        string? cursor, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        database.TenantContext.RequireTenantId();
        if (query?.Length > 200 || query?.Any(char.IsControl) == true)
            return Problem(400, "invalid_query", "Use up to 200 characters without control characters for search.");
        if (group?.Length > 200 || group?.Any(char.IsControl) == true ||
            (materialKind is not null && !GemReferenceInput.MaterialKinds.Contains(materialKind)))
            return Problem(400, "invalid_filter", "Review the material kind and group filters.");
        query = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        group = string.IsNullOrWhiteSpace(group) ? null : group.Trim();
        var search = new GemReferenceSearch(query?.ToUpperInvariant(), materialKind, group?.ToUpperInvariant());
        if (!GemReferenceCursor.TryDecode(cursor, search, out var position))
            return Problem(400, "invalid_cursor", "Refresh the shared reference to start a new page.");
        var afterName = position?.CommonName;
        var afterId = position?.Id;
        var rows = await database.GemReferenceEntries.FromSql($"""
            SELECT entry.* FROM [Gemology].[Entries] entry
            WHERE entry.IsRetired=0
                AND ({materialKind} IS NULL OR entry.MaterialKind COLLATE Latin1_General_100_BIN2={materialKind})
                AND ({group} IS NULL OR entry.[Group] COLLATE Latin1_General_100_CI_AS={group})
                AND ({afterName} IS NULL OR entry.CommonName COLLATE Latin1_General_100_CI_AS>{afterName}
                    OR (entry.CommonName COLLATE Latin1_General_100_CI_AS={afterName} AND entry.Id>{afterId}))
                AND ({query} IS NULL OR CHARINDEX({query},entry.CommonName COLLATE Latin1_General_100_CI_AS)>0
                    OR CHARINDEX({query},entry.[Group] COLLATE Latin1_General_100_CI_AS)>0
                    OR CHARINDEX({query},entry.Species COLLATE Latin1_General_100_CI_AS)>0
                    OR CHARINDEX({query},entry.Variety COLLATE Latin1_General_100_CI_AS)>0
                    OR EXISTS(SELECT 1 FROM [Gemology].[Aliases] alias WHERE alias.EntryId=entry.Id
                        AND CHARINDEX({query},alias.Name COLLATE Latin1_General_100_CI_AS)>0))
            """).AsNoTracking().OrderBy(row => EF.Functions.Collate(row.CommonName, Collation)).ThenBy(row => row.Id)
            .Select(row => new GemReferenceListEntry(row.Id, row.MaterialKind, row.CommonName,
                row.Group, row.Species, row.Variety, "workbenchReference")).Take(51).ToListAsync(cancellationToken);
        var next = rows.Count > 50 ? GemReferenceCursor.Encode(new(rows[49].CommonName, rows[49].Id), search) : null;
        return Results.Ok(new GemReferencePageResponse(rows.Take(50).ToArray(), next));
    }

    private static async Task<IResult> DetailAsync(Guid id, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        database.TenantContext.RequireTenantId();
        // Retain row/range locks through materialization so an atomic publication cannot mix
        // classification, alias, and source values, including when child rows are inserted.
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var row = await database.GemReferenceEntries.AsNoTracking().AsSingleQuery()
            .Include(entry => entry.Aliases).Include(entry => entry.SourceAssertions).Include(entry => entry.NotableLocality)
            .SingleOrDefaultAsync(entry => entry.Id == id, cancellationToken);
        if (row is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return Problem(404, "gem_reference_not_found", "Shared gem reference not found.");
        }
        var response = new GemReferenceDetailResponse(row.Id, row.MaterialKind, row.CommonName,
            row.Group, row.Species, row.Variety, "workbenchReference",
            row.Aliases.OrderBy(alias => alias.Position).Select(alias => alias.Name).ToArray(),
            row.Description, Convert.ToBase64String(row.RowVersion),
            row.SourceAssertions.OrderBy(source => source.Field, StringComparer.Ordinal).ThenBy(source => source.Id)
                .Select(source => new GemReferenceSourceResponse(source.Id, source.Field, source.Title,
                    source.Publisher, source.Url, source.Citation, source.AccessedOn, source.ReviewedOn, "workbench")).ToArray(),
            row.NotableLocality is { } locality ? new(locality.Place, locality.Scope, locality.ReviewedOn, locality.SourceAssertionId) : null,
            new(row.IsRetired, row.RetirementExplanation, row.RedirectEntryId));
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(response);
    }
}
