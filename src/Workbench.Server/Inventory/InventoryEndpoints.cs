// Copyright (c) 2026 The White Stag Collection.

using System.Globalization;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
        group.MapGet("/archived", ListArchivedAsync).Produces<ItemPageResponse>().ProducesProblem(StatusCodes.Status400BadRequest);
        group.MapGet("/{id:guid}", DetailAsync).Produces<ItemDetailResponse>().ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPut("/{id:guid}", UpdateAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces<ItemDetailResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapItemPhotos();
        group.MapItemExport();
        group.MapPost("/{id:guid}/archive", ArchiveAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces<ItemDetailResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPost("/{id:guid}/restore", RestoreAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces<ItemDetailResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(CreateItemRequest request, WorkbenchDbContext database,
        TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        request = ItemInput.Normalize(request);
        var errors = ItemInput.Validate(request);
        if (errors.Count != 0)
            return Results.ValidationProblem(errors);

        var existing = await database.Items.AsNoTracking().Include(row => row.CurrentPhoto).SingleOrDefaultAsync(
            item => item.CreationRequestId == request.CreationRequestId, cancellationToken);
        if (existing is not null)
            return await ReplayAsync(database, existing, request, cancellationToken);

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
            existing = await database.Items.AsNoTracking().Include(row => row.CurrentPhoto).SingleOrDefaultAsync(
                row => row.CreationRequestId == request.CreationRequestId, cancellationToken);
            if (existing is null)
                throw;
            return await ReplayAsync(database, existing, request, cancellationToken);
        }
        return Results.Created($"/api/items/{item.Id}", Detail(item));
    }

    private static async Task<IResult> ReplayAsync(WorkbenchDbContext database, InventoryItem item,
        CreateItemRequest request, CancellationToken cancellationToken)
    {
        // Read after the item: an edited item and its immutable snapshot commit atomically.
        // Without a snapshot the fields read above are still the original creation payload.
        var original = await database.ItemCreationSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(row => row.ItemId == item.Id, cancellationToken);
        var name = original is null ? item.Name : original.Name;
        var notes = original is null ? item.Notes : original.Notes;
        var location = original is null ? item.StorageLocation : original.StorageLocation;
        return name == request.Name && notes == request.Notes && location == request.Location
            ? Results.Ok(Detail(item))
            : Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "This save identifier was already used for different item details.");
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateItemDetailsRequest request,
        WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        var fields = ItemInput.Normalize(new CreateItemRequest(Guid.NewGuid(), request.Name, request.Notes, request.Location));
        var errors = ItemInput.Validate(fields);
        var version = new byte[8];
        if (request.ExpectedVersion is null ||
            !Convert.TryFromBase64String(request.ExpectedVersion, version, out var written) || written != version.Length)
            errors["expectedVersion"] = ["A valid item version is required."];
        if (errors.Count != 0)
            return Results.ValidationProblem(errors);

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await using var command = new SqlCommand("[Inventory].[UpdateItemDetails]", (SqlConnection)database.Database.GetDbConnection(),
            (SqlTransaction)transaction.GetDbTransaction())
        { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.Add(new SqlParameter("@ExpectedVersion", SqlDbType.Binary, 8) { Value = version });
        command.Parameters.Add(new SqlParameter("@Name", SqlDbType.NVarChar, -1) { Value = fields.Name! });
        command.Parameters.Add(new SqlParameter("@Notes", SqlDbType.NVarChar, -1) { Value = (object?)fields.Notes ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@Location", SqlDbType.NVarChar, -1) { Value = (object?)fields.Location ?? DBNull.Value });
        var status = (int)(await command.ExecuteScalarAsync(cancellationToken))!;
        if (status == 0)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Item not found.");
        if (status == 3)
            return ArchivedConflict();
        if (status == 2)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The item changed. Review the current record before saving again.",
                extensions: new Dictionary<string, object?> { ["code"] = "item_version_conflict" });

        // The UPDATE holds its exclusive row lock until this detail has been read and committed.
        var saved = await database.Items.AsNoTracking().Include(row => row.CurrentPhoto).SingleAsync(row => row.Id == id, cancellationToken);
        var response = Detail(saved);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> DetailAsync(Guid id, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        var item = await database.Items.AsNoTracking().Include(row => row.CurrentPhoto).SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
        return item is null ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Item not found.") : Results.Ok(Detail(item));
    }

    private static Task<IResult> ListAsync(string? cursor, string? q, WorkbenchDbContext database, CancellationToken cancellationToken) =>
        ListScopeAsync(cursor, q, false, database, cancellationToken);

    private static Task<IResult> ListArchivedAsync(string? cursor, string? q, WorkbenchDbContext database, CancellationToken cancellationToken) =>
        ListScopeAsync(cursor, q, true, database, cancellationToken);

    private static async Task<IResult> ListScopeAsync(string? cursor, string? q, bool archived, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        q = q?.Trim();
        if (q is { Length: > 200 } || q?.Contains('\0') == true)
            return ApiProblemResults.InvalidRequest("Search must be at most 200 characters and must not contain NUL.");

        var query = database.Items.AsNoTracking();
        query = archived ? query.Where(row => row.ArchivedAtUtc != null) : query.Where(row => row.ArchivedAtUtc == null);
        if (!string.IsNullOrEmpty(q))
        {
            // Explicit collation keeps case/accent behavior independent of database defaults.
            // Contains is translated as a parameterized literal substring, not user-supplied LIKE syntax.
            query = query.Where(row => EF.Functions.Collate(row.Name, "Latin1_General_100_CI_AS_SC").Contains(q) ||
                (row.Notes != null && EF.Functions.Collate(row.Notes, "Latin1_General_100_CI_AS_SC").Contains(q)) ||
                (row.StorageLocation != null && EF.Functions.Collate(row.StorageLocation, "Latin1_General_100_CI_AS_SC").Contains(q)));
        }
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
        var entities = await query.Include(row => row.CurrentPhoto).OrderBy(row => row.CreatedAtUtc).ThenBy(row => row.Id)
            .Take(51).ToListAsync(cancellationToken);
        var rows = entities.Select(row => new ItemSummaryResponse(row.Id, row.Name, row.StorageLocation, row.CreatedAtUtc,
            Photo(row.CurrentPhoto))).ToList();
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
        new(item.Id, item.Name, item.Notes, item.StorageLocation, item.CreatedAtUtc, Convert.ToBase64String(item.RowVersion), Photo(item.CurrentPhoto), item.ArchivedAtUtc);

    private static IResult ArchivedConflict() => Results.Problem(statusCode: StatusCodes.Status409Conflict,
        title: "This record is archived and cannot be changed.",
        extensions: new Dictionary<string, object?> { ["code"] = "item_archived" });

    private static async Task<IResult> ArchiveAsync(Guid id, ArchiveItemRequest request,
        WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        var version = new byte[8];
        if (request.ExpectedVersion is null || !Convert.TryFromBase64String(request.ExpectedVersion, version, out var written) || written != 8)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["expectedVersion"] = ["A valid item version is required."] });
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await using var command = new SqlCommand("[Inventory].[ArchiveItem]", (SqlConnection)database.Database.GetDbConnection(),
            (SqlTransaction)transaction.GetDbTransaction())
        { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.Add(new SqlParameter("@ExpectedVersion", SqlDbType.VarBinary, -1) { Value = version });
        var status = (int)(await command.ExecuteScalarAsync(cancellationToken))!;
        if (status == 0)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Item not found.");
        if (status == 3)
            return ArchivedConflict();
        if (status == 2)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "The item changed. Review the current record before archiving again.",
                extensions: new Dictionary<string, object?> { ["code"] = "item_version_conflict" });
        var saved = await database.Items.AsNoTracking().Include(row => row.CurrentPhoto).SingleAsync(row => row.Id == id, cancellationToken);
        var response = Detail(saved);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> RestoreAsync(Guid id, RestoreItemRequest request,
        WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        var version = new byte[8];
        if (request.ExpectedVersion is null || !Convert.TryFromBase64String(request.ExpectedVersion, version, out var written) || written != 8)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["expectedVersion"] = ["A valid item version is required."] });
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await using var command = new SqlCommand("[Inventory].[RestoreItem]", (SqlConnection)database.Database.GetDbConnection(),
            (SqlTransaction)transaction.GetDbTransaction())
        { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@Id", id);
        command.Parameters.Add(new SqlParameter("@ExpectedVersion", SqlDbType.VarBinary, -1) { Value = version });
        var status = (int)(await command.ExecuteScalarAsync(cancellationToken))!;
        if (status == 0)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Item not found.");
        if (status == 3)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "This record is already in the collection.",
                extensions: new Dictionary<string, object?> { ["code"] = "item_active" });
        if (status == 2)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "The item changed. Review the current record before restoring again.",
                extensions: new Dictionary<string, object?> { ["code"] = "item_version_conflict" });
        var saved = await database.Items.AsNoTracking().Include(row => row.CurrentPhoto).SingleAsync(row => row.Id == id, cancellationToken);
        var response = Detail(saved);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(response);
    }

    private static ItemPhotoResponse? Photo(ItemPhoto? photo) => photo is null ? null : new(photo.Id,
        $"/api/items/{photo.ItemId}/photo/{photo.Id}/thumbnail", $"/api/items/{photo.ItemId}/photo/{photo.Id}/detail", photo.Width, photo.Height);
}
