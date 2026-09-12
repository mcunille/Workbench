// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Authorization;
using Workbench.Server.Http;
using Workbench.Server.Persistence;

namespace Workbench.Server.Purchasing;

public static class DraftOrderEndpoints
{
    public static void MapPurchaseOrderDrafts(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/purchase-order-drafts").WithTags("Purchasing").RequireAuthorization();
        group.MapGet("", BrowseAsync).Produces<DraftOrderPageResponse>().ProducesProblem(400);
        group.MapGet("/{id:guid}", ReadAsync).Produces<DraftOrderResponse>().ProducesProblem(404);
        group.MapPost("", CreateAsync).WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces<SaveDraftOrderResponse>(201).Produces<SaveDraftOrderResponse>()
            .ProducesValidationProblem().ProducesProblem(409).ProducesProblem(413).ProducesProblem(426);
        group.MapPut("/{id:guid}", UpdateAsync).WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces<SaveDraftOrderResponse>().ProducesValidationProblem().ProducesProblem(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(426);
        group.MapDelete("/{id:guid}", DeleteAsync).WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces<SaveDraftOrderResponse>().ProducesValidationProblem().ProducesProblem(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(426);
    }

    private static async Task<IResult> BrowseAsync(string? cursor, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        if (!DraftOrderCursor.TryDecode(cursor, out var timestamp, out var id))
            return Problem(400, "invalid_cursor", "Refresh drafts to start a new page.");
        // The SQL comparison and ORDER BY share SQL Server's native UUID ordering. EF still applies
        // the tenant query filter, independently of the database's row-level security.
        var query = cursor is null ? database.DraftOrders.AsNoTracking().Where(row => !row.IsDeleted) : database.DraftOrders.FromSql(
            $"SELECT * FROM [Purchasing].[DraftOrders] WHERE [IsDeleted] = 0 AND ([UpdatedAtUtc] < {timestamp} OR ([UpdatedAtUtc] = {timestamp} AND [Id] < {id}))").AsNoTracking();
        var rows = await query.OrderByDescending(row => row.UpdatedAtUtc).ThenByDescending(row => row.Id)
            .Take(51).Select(row => new { row.Id, row.Title, row.SupplierName, row.UpdatedAtUtc }).ToListAsync(cancellationToken);
        return Results.Ok(new DraftOrderPageResponse(rows.Take(50).Select(row => new DraftOrderSummary(row.Id, row.Title,
            row.SupplierName, DraftOrderCursor.Timestamp(row.UpdatedAtUtc))).ToArray(),
            rows.Count > 50 ? DraftOrderCursor.Encode(rows[49].UpdatedAtUtc, rows[49].Id) : null));
    }

    private static async Task<IResult> ReadAsync(Guid id, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        var row = await database.DraftOrders.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id && !row.IsDeleted, cancellationToken);
        if (row is null) return Problem(404, "draft_not_found", "Draft not found.");
        using var content = JsonDocument.Parse(row.ContentJson);
        var links = content.RootElement.GetProperty("sourceLinks").Deserialize<string[]>(DraftOrderInput.JsonOptions)!;
        var entries = content.RootElement.GetProperty("entries").Deserialize<DraftEntry[]>(DraftOrderInput.JsonOptions)!;
        return Results.Ok(new DraftOrderResponse(row.Id, new(row.Title, row.SupplierName, row.Currency, row.Notes, links, entries),
            DraftOrderCursor.Timestamp(row.CreatedAtUtc), DraftOrderCursor.Timestamp(row.UpdatedAtUtc), Convert.ToBase64String(row.RowVersion)));
    }

    private static Task<IResult> CreateAsync(CreateDraftOrderRequest request, WorkbenchDbContext database,
        RequestActor actor, HttpContext context, CancellationToken cancellationToken) =>
        SaveAsync(request.RequestId, null, null, request.Draft, database, actor, context, cancellationToken);

    private static Task<IResult> UpdateAsync(Guid id, UpdateDraftOrderRequest request, WorkbenchDbContext database,
        RequestActor actor, HttpContext context, CancellationToken cancellationToken) =>
        SaveAsync(request.RequestId, id, request.ExpectedVersion, request.Draft, database, actor, context, cancellationToken);

    internal static async Task<IResult> DeleteAsync(Guid id, [FromBody] DeleteDraftOrderRequest request,
        WorkbenchDbContext database, RequestActor actor, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.RequestId == Guid.Empty) errors["requestId"] = ["A nonempty deletion request identifier is required."];
        var version = DraftOrderInput.NormalizeVersion(request.ExpectedVersion, errors);
        if (errors.Count > 0) return Validation(errors);
        await database.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            // Deletion owns its transaction and retains immutable receipt evidence for an uncertain retry.
            await using var command = new SqlCommand("[Purchasing].[DeleteDraftOrder]", (SqlConnection)database.Database.GetDbConnection())
            { CommandType = CommandType.StoredProcedure };
            command.Parameters.Add(new SqlParameter("@RequestId", SqlDbType.UniqueIdentifier) { Value = request.RequestId });
            command.Parameters.Add(new SqlParameter("@ActorUserId", SqlDbType.UniqueIdentifier) { Value = actor.UserId });
            command.Parameters.Add(new SqlParameter("@DraftOrderId", SqlDbType.UniqueIdentifier) { Value = id });
            command.Parameters.Add(new SqlParameter("@ExpectedRowVersion", SqlDbType.VarBinary, -1) { Value = Convert.FromBase64String(version!) });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Draft deletion did not return a receipt.");
            return Results.Ok(new SaveDraftOrderResponse(reader.GetGuid(reader.GetOrdinal("RequestId")),
                reader.GetBoolean(reader.GetOrdinal("Replayed")), reader.GetGuid(reader.GetOrdinal("DraftOrderId")),
                Convert.ToBase64String((byte[])reader["SavedVersion"]),
                DraftOrderCursor.Timestamp(reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("CompletedAtUtc")))));
        }
        catch (SqlException exception) when (exception.Number is 50400 or 50403 or 50404 or 50409 or 50410 or 50426)
        {
            return exception.Number switch
            {
                50400 => Validation(new() { ["requestId"] = ["Supply a valid deletion identifier and saved version."] }),
                50403 => Problem(403, "draft_authority_required", "Current business authority is required."),
                50404 => Problem(404, "draft_not_found", "Draft not found."),
                50409 => Problem(409, "draft_version_conflict", "The draft changed. Review the saved version before deleting it."),
                50426 => Problem(426, "draft_contract_reload_required", "Reload this draft with the current application before saving."),
                _ => Problem(409, "draft_request_conflict", "This request identifier was already used for different input."),
            };
        }
        finally { await database.Database.CloseConnectionAsync(); }
    }

    private static async Task<IResult> SaveAsync(Guid requestId, Guid? id, string? version, DraftContent? input,
        WorkbenchDbContext database, RequestActor actor, HttpContext context, CancellationToken cancellationToken)
    {
        var draft = input is null ? null : DraftOrderInput.Normalize(input);
        var errors = DraftOrderInput.Validate(draft);
        if (requestId == Guid.Empty) errors["requestId"] = ["A nonempty save identifier is required."];
        var expectedVersion = id is null ? null : DraftOrderInput.NormalizeVersion(version, errors);
        if (errors.Count > 0) return Validation(errors);
        var operation = id is null ? "Create" : "Update";
        await database.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            // The command owns its transaction and computes the fingerprint over this very document.
            await using var command = new SqlCommand($"[Purchasing].[{operation}DraftOrder]", (SqlConnection)database.Database.GetDbConnection())
            { CommandType = CommandType.StoredProcedure };
            command.Parameters.Add(new SqlParameter("@RequestId", SqlDbType.UniqueIdentifier) { Value = requestId });
            command.Parameters.Add(new SqlParameter("@ActorUserId", SqlDbType.UniqueIdentifier) { Value = actor.UserId });
            command.Parameters.Add(new SqlParameter("@CanonicalInputJson", SqlDbType.NVarChar, -1)
            { Value = DraftOrderInput.Canonical(operation, id, expectedVersion, draft!) });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Draft save did not return a receipt.");
            var response = new SaveDraftOrderResponse(reader.GetGuid(reader.GetOrdinal("RequestId")),
                reader.GetBoolean(reader.GetOrdinal("Replayed")), reader.GetGuid(reader.GetOrdinal("DraftOrderId")),
                Convert.ToBase64String((byte[])reader["SavedVersion"]),
                DraftOrderCursor.Timestamp(reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("CompletedAtUtc"))));
            var location = $"/api/purchase-order-drafts/{response.DraftOrderId:D}";
            if (id is null) context.Response.Headers.Location = location;
            return id is null && !response.Replayed ? Results.Created(location, response) : Results.Ok(response);
        }
        catch (SqlException exception) when (exception.Number is 50400 or 50401 or 50403 or 50404 or 50409 or 50410 or 50426)
        {
            return exception.Number switch
            {
                50400 => Validation(new() { ["draft"] = ["Review the draft fields and limits."] }),
                50401 => Validation(new() { ["draft.currency"] = ["Clear existing prices and save before entering amounts in a different currency."] }),
                50403 => Problem(403, "draft_authority_required", "Current business authority is required."),
                50404 => Problem(404, "draft_not_found", "Draft not found."),
                50409 => Problem(409, "draft_version_conflict", "The draft changed. Review the saved version before saving again."),
                50426 => Problem(426, "draft_contract_reload_required", "Reload this draft with the current application before saving."),
                _ => Problem(409, "draft_request_conflict", "This save identifier was already used for different input."),
            };
        }
        finally { await database.Database.CloseConnectionAsync(); }
    }

    private static IResult Validation(Dictionary<string, string[]> errors) => Results.ValidationProblem(errors,
        title: "Review the draft fields.", type: "about:blank", extensions: new Dictionary<string, object?> { ["code"] = "draft_validation_failed" });

    private static IResult Problem(int status, string code, string title) => Results.Problem(statusCode: status,
        title: title, type: "about:blank", extensions: new Dictionary<string, object?> { ["code"] = code });
}
