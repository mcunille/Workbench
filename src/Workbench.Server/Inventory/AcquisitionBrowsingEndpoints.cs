// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Workbench.Server.Http;
using Workbench.Server.Persistence;

namespace Workbench.Server.Inventory;

public static partial class AcquisitionEndpoints
{
    public static void MapAcquisitionBrowsing(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/acquisitions").WithTags("Inventory").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            return await next(context);
        });
        group.MapGet("", DiscoverAsync).Produces<AcquisitionPageResponse>().ProducesProblem(400);
        group.MapGet("/{id:guid}", AcquisitionAsync).Produces<AcquisitionResponse>().ProducesProblem(404);
        group.MapGet("/{id:guid}/items", ItemsAsync).Produces<AcquisitionItemsResponse>().ProducesProblem(400).ProducesProblem(404);
    }

    private static AcquisitionResponse Context(Acquisition row) => new(row.Id, row.Method, row.Source,
        row.Year, row.Month, row.Day, row.Notes, Convert.ToBase64String(row.RowVersion));

    private static bool Cursor(string? cursor, out DateTimeOffset timestamp, out Guid id)
    {
        timestamp = default;
        id = default;
        if (cursor is null) return true;
        var parts = cursor.Split('_');
        return parts.Length == 2 && DateTimeOffset.TryParseExact(parts[0], "O", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out timestamp) && timestamp.Offset == TimeSpan.Zero && Guid.TryParseExact(parts[1], "N", out id) && id != Guid.Empty;
    }

    private static string Encode(DateTimeOffset timestamp, Guid id) => $"{timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}_{id:N}";

    private static async Task<IResult> DiscoverAsync(string? search, string? cursor, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        if (search is { Length: > 200 } || search?.Contains('\0') == true || !Cursor(cursor, out var timestamp, out var id))
            return ApiProblemResults.InvalidRequest("Use a search of at most 200 characters and a valid acquisition cursor.");
        search = search?.Trim();
        var query = database.Acquisitions.AsNoTracking();
        if (!string.IsNullOrEmpty(search))
            query = query.Where(row => (row.Source != null && EF.Functions.Collate(row.Source, "Latin1_General_100_CI_AS_SC").Contains(search)) ||
                (row.Notes != null && EF.Functions.Collate(row.Notes, "Latin1_General_100_CI_AS_SC").Contains(search)));
        if (cursor is not null) query = query.Where(row => row.CreatedAtUtc > timestamp || (row.CreatedAtUtc == timestamp && row.Id.CompareTo(id) > 0));
        var rows = await query.OrderBy(row => row.CreatedAtUtc).ThenBy(row => row.Id).Take(41).ToListAsync(cancellationToken);
        var next = rows.Count > 40 ? Encode(rows[39].CreatedAtUtc, rows[39].Id) : null;
        return Results.Ok(new AcquisitionPageResponse(rows.Take(40).Select(Context).ToList(), next));
    }

    private static async Task<IResult> AcquisitionAsync(Guid id, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        var row = await database.Acquisitions.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
        return row is null ? Failure(0) : Results.Ok(Context(row));
    }

    private static async Task<IResult> ItemsAsync(Guid id, bool? includeArchived, string? cursor, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        if (!Cursor(cursor, out var timestamp, out var afterId)) return ApiProblemResults.InvalidRequest("Invalid acquisition pieces cursor.");
        if (!await database.Acquisitions.AnyAsync(row => row.Id == id, cancellationToken)) return Failure(0);
        var query = database.Items.AsNoTracking().Where(item => database.AcquisitionItems.Any(link => link.ItemId == item.Id && link.AcquisitionId == id));
        if (includeArchived != true) query = query.Where(row => row.ArchivedAtUtc == null);
        if (cursor is not null) query = query.Where(row => row.CreatedAtUtc > timestamp || (row.CreatedAtUtc == timestamp && row.Id.CompareTo(afterId) > 0));
        var rows = await query.Include(row => row.CurrentPhoto).OrderBy(row => row.CreatedAtUtc).ThenBy(row => row.Id).Take(41).ToListAsync(cancellationToken);
        var next = rows.Count > 40 ? Encode(rows[39].CreatedAtUtc, rows[39].Id) : null;
        return Results.Ok(new AcquisitionItemsResponse(rows.Take(40).Select(InventoryEndpoints.Detail).ToList(), next));
    }

    private static async Task<IResult> LinkAsync(Guid id, LinkAcquisitionRequest request, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var itemVersion = AcquisitionInput.Version(request.ExpectedItemVersion, "expectedItemVersion", errors);
        byte[]? ValidatePair(Guid? acquisitionId, string? version, string field)
        {
            if (acquisitionId == Guid.Empty || (acquisitionId is null && version is not null)) errors[field] = ["An acquisition identifier and saved version must be supplied together."];
            return acquisitionId is null ? null : AcquisitionInput.Version(version, field, errors);
        }
        var oldVersion = ValidatePair(request.ExpectedAcquisitionId, request.ExpectedAcquisitionVersion, "expectedAcquisitionVersion");
        var newVersion = ValidatePair(request.TargetAcquisitionId, request.TargetAcquisitionVersion, "targetAcquisitionVersion");
        if (errors.Count != 0) return Results.ValidationProblem(errors);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await using var command = new SqlCommand("[Inventory].[ChangeAcquisitionLink]", (SqlConnection)database.Database.GetDbConnection(),
            (SqlTransaction)transaction.GetDbTransaction())
        { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@ItemId", id);
        command.Parameters.Add(new SqlParameter("@ExpectedItemVersion", SqlDbType.VarBinary, -1) { Value = itemVersion });
        command.Parameters.Add(new SqlParameter("@ExpectedAcquisitionId", SqlDbType.UniqueIdentifier) { Value = (object?)request.ExpectedAcquisitionId ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@TargetAcquisitionId", SqlDbType.UniqueIdentifier) { Value = (object?)request.TargetAcquisitionId ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@ExpectedAcquisitionVersion", SqlDbType.VarBinary, -1) { Value = (object?)oldVersion ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@TargetAcquisitionVersion", SqlDbType.VarBinary, -1) { Value = (object?)newVersion ?? DBNull.Value });
        var status = (int)(await command.ExecuteScalarAsync(cancellationToken))!;
        if (status != 1) return Failure(status);
        var response = await ReadSavedAsync(id, database, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(response);
    }
}
