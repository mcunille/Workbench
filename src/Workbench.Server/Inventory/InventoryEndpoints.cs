// Copyright (c) 2026 The White Stag Collection.

using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Http;
using Workbench.Server.Persistence;

namespace Workbench.Server.Inventory;

public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapWorkbenchInventory(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/items").WithTags("Inventory").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            return await next(context);
        });
        group.MapPost("", CreateAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces<ItemDetailResponse>(StatusCodes.Status201Created)
            .Produces<ItemDetailResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapGet("", ListAsync).Produces<ItemPageResponse>().ProducesProblem(StatusCodes.Status400BadRequest);
        group.MapGet("/{id:guid}", DetailAsync).Produces<ItemDetailResponse>().ProducesProblem(StatusCodes.Status404NotFound);
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(CreateItemRequest request, WorkbenchDbContext database,
        TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        request = ItemInput.Normalize(request);
        var errors = ItemInput.Validate(request);
        if (errors.Count != 0)
            return Results.ValidationProblem(errors);

        var existing = await database.Items.AsNoTracking().SingleOrDefaultAsync(
            item => item.CreationRequestId == request.CreationRequestId, cancellationToken);
        if (existing is not null)
            return Replay(existing, request);

        var item = new InventoryItem
        {
            Id = Guid.NewGuid(),
            TenantId = database.TenantContext.RequireTenantId(),
            Name = request.Name!,
            Notes = request.Notes,
            StorageLocation = request.Location,
            CreationRequestId = request.CreationRequestId,
            CreatedAtUtc = timeProvider.GetUtcNow(),
        };
        database.Items.Add(item);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // The unique tenant/request index arbitrates competing saves. A failed INSERT's
            // implicit transaction has ended; read the committed winner through the tenant filter.
            database.Entry(item).State = EntityState.Detached;
            existing = await database.Items.AsNoTracking().SingleOrDefaultAsync(
                row => row.CreationRequestId == request.CreationRequestId, cancellationToken);
            if (existing is null)
                throw;
            return Replay(existing, request);
        }
        return Results.Created($"/api/items/{item.Id}", Detail(item));
    }

    private static IResult Replay(InventoryItem item, CreateItemRequest request) =>
        item.Name == request.Name && item.Notes == request.Notes && item.StorageLocation == request.Location
            ? Results.Ok(Detail(item))
            : Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "This save identifier was already used for different item details.");

    private static async Task<IResult> DetailAsync(Guid id, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        var item = await database.Items.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
        return item is null ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Item not found.") : Results.Ok(Detail(item));
    }

    private static async Task<IResult> ListAsync(string? cursor, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        var query = database.Items.AsNoTracking();
        if (cursor is not null)
        {
            var parts = cursor.Split('_');
            if (parts.Length != 2 || !DateTimeOffset.TryParseExact(parts[0], "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var timestamp) || timestamp.Offset != TimeSpan.Zero ||
                !Guid.TryParseExact(parts[1], "N", out var id) || id == Guid.Empty)
                return ApiProblemResults.InvalidRequest("Invalid collection cursor.");
            // EF translates Guid.CompareTo to SQL comparisons, preserving SQL Server UUID order.
            query = query.Where(row => row.CreatedAtUtc > timestamp ||
                (row.CreatedAtUtc == timestamp && row.Id.CompareTo(id) > 0));
        }
        var rows = await query.OrderBy(row => row.CreatedAtUtc).ThenBy(row => row.Id)
            .Take(51).Select(row => new ItemSummaryResponse(row.Id, row.Name, row.StorageLocation, row.CreatedAtUtc))
            .ToListAsync(cancellationToken);
        string? nextCursor = null;
        if (rows.Count > 50)
        {
            rows.RemoveAt(50);
            var last = rows[^1];
            nextCursor = $"{last.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}_{last.Id:N}";
        }
        return Results.Ok(new ItemPageResponse(rows, nextCursor));
    }

    private static ItemDetailResponse Detail(InventoryItem item) =>
        new(item.Id, item.Name, item.Notes, item.StorageLocation, item.CreatedAtUtc);
}
