// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Authorization;
using Workbench.Server.Http;
using Workbench.Server.Persistence;

namespace Workbench.Server.Purchasing;

public static class DraftOrderEndpointsV2
{
    public static void MapPurchaseOrderDraftsV2(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v2/purchase-order-drafts").WithTags("Purchasing").RequireAuthorization();
        group.MapGet("", BrowseAsync).Produces<DraftOrderPageResponseV2>().ProducesProblem(400);
        group.MapGet("/{id:guid}", ReadAsync).Produces<DraftOrderResponseV2>().ProducesProblem(404);
        group.MapPost("", CreateAsync).WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces<SaveDraftOrderResponse>(201).Produces<SaveDraftOrderResponse>()
            .ProducesValidationProblem().ProducesProblem(404).ProducesProblem(409).ProducesProblem(413);
        group.MapPut("/{id:guid}", UpdateAsync).WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces<SaveDraftOrderResponse>().ProducesValidationProblem().ProducesProblem(404).ProducesProblem(409).ProducesProblem(413);
        group.MapDelete("/{id:guid}", DraftOrderEndpoints.DeleteAsync).WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces<SaveDraftOrderResponse>().ProducesValidationProblem().ProducesProblem(404).ProducesProblem(409).ProducesProblem(413);
    }

    private static async Task<IResult> BrowseAsync(string? cursor, string? query, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        query = PurchasingIdentityInput.Query(query);
        if (query?.Length > 200 || query?.Any(char.IsControl) == true) return Problem(400, "invalid_query", "Use up to 200 characters for search.");
        var binding = database.TenantContext.RequireTenantId().ToString("N") + ":" + query;
        if (!PurchasingIdentityInput.Decode(cursor, binding, out var timestamp, out var id)) return Problem(400, "invalid_cursor", "Refresh drafts to start a new page.");
        var rowsQuery = database.DraftOrders.FromSql($"SELECT * FROM [Purchasing].[DraftOrders] WHERE IsDeleted=0 AND ({cursor} IS NULL OR [UpdatedAtUtc] < {timestamp} OR ([UpdatedAtUtc] = {timestamp} AND [Id] < {id})) AND ({query} IS NULL OR CHARINDEX({query},UPPER(Title) COLLATE Latin1_General_100_CI_AS)>0 OR CHARINDEX({query},UPPER(SupplierName) COLLATE Latin1_General_100_CI_AS)>0 OR CHARINDEX({query},UPPER(SupplierOrderReference) COLLATE Latin1_General_100_CI_AS)>0 OR CHARINDEX({query},'PO-'+CASE WHEN PoNumber<1000000 THEN RIGHT('000000'+CONVERT(varchar(20),PoNumber),6) ELSE CONVERT(varchar(20),PoNumber) END)>0)").AsNoTracking();
        var rows = await rowsQuery.OrderByDescending(row => row.UpdatedAtUtc).ThenByDescending(row => row.Id).Take(51).ToListAsync(cancellationToken);
        return Results.Ok(new DraftOrderPageResponseV2(rows.Take(50).Select(row => new DraftOrderSummaryV2(row.Id, row.Title, row.SupplierName, DraftOrderCursor.Timestamp(row.UpdatedAtUtc), PurchasingIdentityInput.Reference(row.PoNumber), row.SupplierOrderReference, row.Platform)).ToArray(), rows.Count > 50 ? PurchasingIdentityInput.Cursor(rows[49].UpdatedAtUtc, rows[49].Id, binding) : null));
    }
    private static async Task<IResult> ReadAsync(Guid id, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        var row = await database.DraftOrders.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id && !row.IsDeleted, cancellationToken);
        if (row is null) return Problem(404, "draft_not_found", "Draft not found.");
        using var content = JsonDocument.Parse(row.ContentJson);
        var links = content.RootElement.GetProperty("sourceLinks").Deserialize<string[]>(DraftOrderInput.JsonOptions)!;
        var entries = content.RootElement.GetProperty("entries").Deserialize<DraftEntry[]>(DraftOrderInput.JsonOptions)!;
        var archived = row.SupplierId is { } supplierId && await database.Suppliers.AnyAsync(s => s.Id == supplierId && s.IsArchived, cancellationToken);
        return Results.Ok(new DraftOrderResponseV2(row.Id, new(row.Title, row.SupplierName, row.Currency, row.Notes, links, entries, row.SupplierId, row.SupplierContactName, row.SupplierEmail, row.SupplierPhone, row.SupplierWebsite, row.SupplierPostalAddress, row.SupplierOrderReference, row.Platform), DraftOrderCursor.Timestamp(row.CreatedAtUtc), DraftOrderCursor.Timestamp(row.UpdatedAtUtc), Convert.ToBase64String(row.RowVersion), PurchasingIdentityInput.Reference(row.PoNumber), archived));
    }
    private static Task<IResult> CreateAsync(CreateDraftOrderRequestV2 request, WorkbenchDbContext database,
        RequestActor actor, HttpContext context, CancellationToken cancellationToken) =>
        SaveAsync(request.RequestId, null, null, request.Draft, database, actor, context, cancellationToken);

    private static Task<IResult> UpdateAsync(Guid id, UpdateDraftOrderRequestV2 request, WorkbenchDbContext database,
        RequestActor actor, HttpContext context, CancellationToken cancellationToken) =>
        SaveAsync(request.RequestId, id, request.ExpectedVersion, request.Draft, database, actor, context, cancellationToken);

    private static async Task<IResult> SaveAsync(Guid requestId, Guid? id, string? version, DraftContentV2? input,
        WorkbenchDbContext database, RequestActor actor, HttpContext context, CancellationToken cancellationToken)
    {
        var draft = input is null ? null : PurchasingIdentityInput.Normalize(input);
        var errors = PurchasingIdentityInput.Validate(draft);
        if (requestId == Guid.Empty) errors["requestId"] = ["A nonempty save identifier is required."];
        var expectedVersion = id is null ? null : DraftOrderInput.NormalizeVersion(version, errors);
        if (errors.Count > 0) return Validation(errors);
        var operation = id is null ? "Create" : "Update";
        await database.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            // The command owns its transaction and computes the fingerprint over this very document.
            await using var command = new SqlCommand($"[Purchasing].[{operation}DraftOrderV2]", (SqlConnection)database.Database.GetDbConnection())
            { CommandType = CommandType.StoredProcedure };
            command.Parameters.Add(new SqlParameter("@RequestId", SqlDbType.UniqueIdentifier) { Value = requestId });
            command.Parameters.Add(new SqlParameter("@ActorUserId", SqlDbType.UniqueIdentifier) { Value = actor.UserId });
            command.Parameters.Add(new SqlParameter("@CanonicalInputJson", SqlDbType.NVarChar, -1)
            { Value = PurchasingIdentityInput.Canonical(operation, id, expectedVersion, draft!) });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Draft save did not return a receipt.");
            var response = new SaveDraftOrderResponse(reader.GetGuid(reader.GetOrdinal("RequestId")),
                reader.GetBoolean(reader.GetOrdinal("Replayed")), reader.GetGuid(reader.GetOrdinal("DraftOrderId")),
                Convert.ToBase64String((byte[])reader["SavedVersion"]),
                DraftOrderCursor.Timestamp(reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("CompletedAtUtc"))));
            var location = $"/api/v2/purchase-order-drafts/{response.DraftOrderId:D}";
            if (id is null) context.Response.Headers.Location = location;
            return id is null && !response.Replayed ? Results.Created(location, response) : Results.Ok(response);
        }
        catch (SqlException exception) when (exception.Number is 50400 or 50401 or 50403 or 50404 or 50409 or 50410 or 50412 or 50413 or 50414)
        {
            return exception.Number switch
            {
                50400 => Validation(new() { ["draft"] = ["Review the draft fields and limits."] }),
                50401 => Validation(new() { ["draft.currency"] = ["Clear existing prices and save before entering amounts in a different currency."] }),
                50403 => Problem(403, "draft_authority_required", "Current business authority is required."),
                50404 => Problem(404, "draft_not_found", "Draft not found."),
                50409 => Problem(409, "draft_version_conflict", "The draft changed. Review the saved version before saving again."),
                50414 => Problem(404, "supplier_not_found", "Supplier not found."),
                50412 => Problem(409, "supplier_selection_conflict", "Choose an active supplier or keep these details as one-off."),
                50413 => Problem(409, "po_sequence_exhausted", "Purchase numbering has reached its limit."),
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
