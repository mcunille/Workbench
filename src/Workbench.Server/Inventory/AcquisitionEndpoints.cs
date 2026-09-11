// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Workbench.Server.Http;
using Workbench.Server.Persistence;

namespace Workbench.Server.Inventory;

public static partial class AcquisitionEndpoints
{
    public static RouteGroupBuilder MapAcquisitions(this RouteGroupBuilder group)
    {
        group.MapGet("/{id:guid}/acquisition", ReadAsync)
            .Produces<ItemAcquisitionResponse>().ProducesProblem(StatusCodes.Status404NotFound);
        group.MapPost("/{id:guid}/acquisition", CreateAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces<ItemAcquisitionResponse>(StatusCodes.Status201Created).Produces<ItemAcquisitionResponse>()
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPut("/{id:guid}/acquisition/{acquisitionId:guid}", UpdateAsync)
            .WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces<ItemAcquisitionResponse>().ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        group.MapPut("/{id:guid}/acquisition-link", LinkAsync).WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces<ItemAcquisitionResponse>().ProducesValidationProblem().ProducesProblem(404).ProducesProblem(409);
        return group;
    }

    private static async Task<ItemAcquisitionResponse?> ReadSavedAsync(Guid id, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        // The caller's item lock keeps both versions stable through this joined read.
        var saved = await (from item in database.Items.AsNoTracking()
                           where item.Id == id
                           join link in database.AcquisitionItems.AsNoTracking().Include(row => row.Acquisition)
                               on new { item.TenantId, ItemId = item.Id } equals new { link.TenantId, link.ItemId } into links
                           from link in links.DefaultIfEmpty()
                           select new { item.RowVersion, Acquisition = link == null ? null : link.Acquisition })
            .SingleOrDefaultAsync(cancellationToken);
        if (saved is null) return null;
        var acquisition = saved.Acquisition;
        return new(acquisition is null ? null : new(acquisition.Id, acquisition.Method, acquisition.Source,
            acquisition.Year, acquisition.Month, acquisition.Day, acquisition.Notes, Convert.ToBase64String(acquisition.RowVersion)),
            Convert.ToBase64String(saved.RowVersion));
    }

    private static async Task<IResult> ReadAsync(Guid id, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        // Under locking READ COMMITTED, one join alone can observe a token before a concurrent
        // context update. Acquire the same item-first lock as writers before reading either token.
        var item = await database.Items.FromSql($"SELECT * FROM [Inventory].[Items] WITH (UPDLOCK,HOLDLOCK) WHERE [Id]={id}")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (item is null) return Failure(0);
        var saved = await ReadSavedAsync(id, database, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return saved is null ? Failure(0) : Results.Ok(saved);
    }

    private static Task<IResult> CreateAsync(Guid id, CreateAcquisitionRequest request, WorkbenchDbContext database,
        TimeProvider timeProvider, CancellationToken cancellationToken) =>
        SaveAsync(id, null, request, null, database, timeProvider, cancellationToken);

    private static Task<IResult> UpdateAsync(Guid id, Guid acquisitionId, UpdateAcquisitionRequest request,
        WorkbenchDbContext database, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        SaveAsync(id, acquisitionId, new(Guid.NewGuid(), request.ExpectedItemVersion, request.Method, request.Source,
            request.Year, request.Month, request.Day, request.Notes), request.ExpectedAcquisitionVersion, database, timeProvider, cancellationToken);

    private static async Task<IResult> SaveAsync(Guid id, Guid? acquisitionId, CreateAcquisitionRequest request,
        string? expectedAcquisitionVersion, WorkbenchDbContext database, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        request = AcquisitionInput.Normalize(request);
        var errors = AcquisitionInput.Validate(request, timeProvider);
        var itemVersion = AcquisitionInput.Version(request.ExpectedItemVersion, "expectedItemVersion", errors);
        var acquisitionVersion = acquisitionId is null ? null : AcquisitionInput.Version(expectedAcquisitionVersion, "expectedAcquisitionVersion", errors);
        if (errors.Count != 0) return Results.ValidationProblem(errors);

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await using var command = new SqlCommand(acquisitionId is null ? "[Inventory].[CreateAcquisition]" : "[Inventory].[UpdateAcquisition]",
            (SqlConnection)database.Database.GetDbConnection(), (SqlTransaction)transaction.GetDbTransaction())
        { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@ItemId", id);
        command.Parameters.Add(new SqlParameter("@ExpectedItemVersion", SqlDbType.VarBinary, -1) { Value = itemVersion });
        if (acquisitionId is null)
            command.Parameters.AddWithValue("@CreationRequestId", request.CreationRequestId);
        else
        {
            command.Parameters.AddWithValue("@AcquisitionId", acquisitionId.Value);
            command.Parameters.Add(new SqlParameter("@ExpectedAcquisitionVersion", SqlDbType.VarBinary, -1) { Value = acquisitionVersion! });
        }
        command.Parameters.Add(new SqlParameter("@Method", SqlDbType.NVarChar, -1) { Value = request.Method! });
        command.Parameters.Add(new SqlParameter("@Source", SqlDbType.NVarChar, -1) { Value = (object?)request.Source ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@Notes", SqlDbType.NVarChar, -1) { Value = (object?)request.Notes ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@Year", SqlDbType.Int) { Value = (object?)request.Year ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@Month", SqlDbType.Int) { Value = (object?)request.Month ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@Day", SqlDbType.Int) { Value = (object?)request.Day ?? DBNull.Value });
        int status;
        try { status = (int)(await command.ExecuteScalarAsync(cancellationToken))!; }
        catch (SqlException exception) when (exception.Number == 50045)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["year"] = ["Enter a valid acquisition date that is not in the future."] });
        }
        if (status is not (1 or 4)) return Failure(status);
        var response = await ReadSavedAsync(id, database, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return acquisitionId is null && status == 1 ? Results.Created($"/api/items/{id}/acquisition", response) : Results.Ok(response);
    }

    private static IResult Failure(int status)
    {
        if (status == 0) return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Acquisition or item not found.");
        var (code, title) = status switch
        {
            3 => ("item_archived", "This record is archived and cannot be changed."),
            5 => ("acquisition_request_conflict", "This save identifier was already used for different acquisition details."),
            6 => ("item_acquisition_conflict", "This item already has an acquisition. Review its saved context."),
            _ => ("acquisition_version_conflict", "The item or acquisition changed. Review the saved context before saving again."),
        };
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: title,
            extensions: new Dictionary<string, object?> { ["code"] = code });
    }
}
