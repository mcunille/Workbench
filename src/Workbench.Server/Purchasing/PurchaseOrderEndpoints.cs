// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Authorization;
using Workbench.Server.Http;
using Workbench.Server.Persistence;
namespace Workbench.Server.Purchasing;

public static class PurchaseOrderEndpoints
{
    public static void MapPurchaseOrders(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/beta/purchase-orders").WithTags("Purchasing").RequireAuthorization();
        group.MapPurchaseOrderDocuments();
        group.MapGet("", BrowseAsync).Produces<PurchaseOrderPageResponse>().ProducesProblem(400);
        group.MapGet("/{id:guid}", ReadAsync).Produces<PurchaseOrderResponse>().ProducesProblem(404);
        group.MapPost("/{id:guid}/amendments", AmendAsync).WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces<SavePurchaseOrderResponse>().ProducesValidationProblem().ProducesProblem(404).ProducesProblem(409).ProducesProblem(413);
        group.MapGet("/{id:guid}/revisions", HistoryAsync).Produces<PurchaseOrderRevisionPageResponse>().ProducesProblem(400).ProducesProblem(404);
        group.MapGet("/{id:guid}/revisions/{revision:int}", RevisionAsync).Produces<PurchaseOrderRevisionResponse>().ProducesProblem(404);
        endpoints.MapPost("/api/beta/purchase-order-drafts/{id:guid}/commit", CommitAsync).WithTags("Purchasing").RequireAuthorization().WithMetadata(WorkbenchAntiforgeryMetadata.Instance)
            .Produces<SavePurchaseOrderResponse>().ProducesValidationProblem().ProducesProblem(404).ProducesProblem(409).ProducesProblem(413);
    }

    internal static DraftContent Content(DraftOrder row)
    {
        using var content = JsonDocument.Parse(row.ContentJson);
        return new(row.Title, row.SupplierName, row.Currency, row.Notes,
            content.RootElement.GetProperty("sourceLinks").Deserialize<string[]>(DraftOrderInput.JsonOptions)!,
            DraftOrderInput.ReadEntries(content.RootElement, row.ContentSchemaVersion), row.SupplierId, row.SupplierContactName, row.SupplierEmail,
            row.SupplierPhone, row.SupplierWebsite, row.SupplierPostalAddress, row.SupplierOrderReference, row.Platform,
            row.ContentSchemaVersion >= 4 ? content.RootElement.GetProperty("orderDiscount").Deserialize<DraftDiscount>(DraftOrderInput.JsonOptions) : null,
            row.ContentSchemaVersion >= 4 ? content.RootElement.GetProperty("charges").Deserialize<DraftCharge[]>(DraftOrderInput.JsonOptions)! : []);
    }
    private static async Task<IResult> BrowseAsync(string? cursor, string? query, string? state, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        query = PurchasingIdentityInput.Query(query);
        if (query?.Length > 200 || query?.Any(char.IsControl) == true || state is not (null or "Draft" or "Ordered")) return Problem(400, "invalid_query", "Choose a purchase state and search of up to 200 characters.");
        var binding = database.TenantContext.RequireTenantId().ToString("N") + ":purchases:" + state + ":" + query;
        if (!PurchasingIdentityInput.Decode(cursor, binding, out var timestamp, out var id)) return Problem(400, "invalid_cursor", "Refresh purchases to start a new page.");
        var rows = await database.DraftOrders.FromSql($"SELECT * FROM Purchasing.DraftOrders WHERE IsDeleted=0 AND ({state} IS NULL OR State={state}) AND ({cursor} IS NULL OR UpdatedAtUtc<{timestamp} OR (UpdatedAtUtc={timestamp} AND Id<{id})) AND ({query} IS NULL OR CHARINDEX({query},UPPER(Title) COLLATE Latin1_General_100_CI_AS)>0 OR CHARINDEX({query},UPPER(SupplierName) COLLATE Latin1_General_100_CI_AS)>0 OR CHARINDEX({query},UPPER(SupplierOrderReference) COLLATE Latin1_General_100_CI_AS)>0 OR CHARINDEX({query},'PO-'+CASE WHEN PoNumber<1000000 THEN RIGHT('000000'+CONVERT(varchar(20),PoNumber),6) ELSE CONVERT(varchar(20),PoNumber) END)>0)")
            .AsNoTracking().OrderByDescending(r => r.UpdatedAtUtc).ThenByDescending(r => r.Id).Take(51).ToListAsync(cancellationToken);
        return Results.Ok(new PurchaseOrderPageResponse(rows.Take(50).Select(r => new PurchaseOrderSummary(r.Id, r.Title, r.SupplierName, DraftOrderCursor.Timestamp(r.UpdatedAtUtc), PurchasingIdentityInput.Reference(r.PoNumber), r.SupplierOrderReference, r.Platform, r.State, r.OrderDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.Revision)).ToArray(), rows.Count > 50 ? PurchasingIdentityInput.Cursor(rows[49].UpdatedAtUtc, rows[49].Id, binding) : null));
    }
    private static async Task<IResult> ReadAsync(Guid id, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        var row = await database.DraftOrders.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && !r.IsDeleted, cancellationToken);
        if (row is null) return Problem(404, "purchase_not_found", "Purchase not found.");
        var draft = Content(row);
        var archived = row.SupplierId is { } supplierId && await database.Suppliers.AnyAsync(s => s.Id == supplierId && s.IsArchived, cancellationToken);
        var calculation = DraftOrderInput.Calculate(draft);
        if (row.State == "Ordered")
        {
            var revision = await Revisions(database, id).SingleAsync(r => r.Revision == row.Revision, cancellationToken);
            calculation = JsonSerializer.Deserialize<DraftCalculationResponse>(revision.CalculationJson, DraftOrderInput.JsonOptions)!;
        }
        return Results.Ok(new PurchaseOrderResponse(row.Id, draft, DraftOrderCursor.Timestamp(row.CreatedAtUtc), DraftOrderCursor.Timestamp(row.UpdatedAtUtc), Convert.ToBase64String(row.RowVersion), PurchasingIdentityInput.Reference(row.PoNumber), archived, calculation, row.State, row.OrderDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), row.Revision));
    }
    private static Task<IResult> CommitAsync(Guid id, CommitPurchaseOrderRequest request, WorkbenchDbContext database, RequestActor actor, CancellationToken cancellationToken) =>
        SaveAsync(id, request.RequestId, request.ExpectedVersion, request.OrderDate, null, null, database, actor, cancellationToken);
    private static Task<IResult> AmendAsync(Guid id, AmendPurchaseOrderRequest request, WorkbenchDbContext database, RequestActor actor, CancellationToken cancellationToken) =>
        SaveAsync(id, request.RequestId, request.ExpectedVersion, request.OrderDate, request.Reason, request.Draft, database, actor, cancellationToken, true);
    private static async Task<IResult> SaveAsync(Guid id, Guid requestId, string version, string orderDate, string? reason, DraftContent? input, WorkbenchDbContext database, RequestActor actor, CancellationToken cancellationToken, bool amendment = false)
    {
        var errors = new Dictionary<string, string[]>();
        var expected = DraftOrderInput.NormalizeVersion(version, errors);
        if (requestId == Guid.Empty) errors["requestId"] = ["Supply a nonempty request identifier."];
        if (errors.Count > 0) return Validation(errors);
        var row = await database.DraftOrders.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && !r.IsDeleted, cancellationToken);
        if (row is null) return Problem(404, "purchase_not_found", "Purchase not found.");
        var draft = amendment ? input is null ? null : DraftOrderInput.Normalize(input) : Content(row);
        reason = reason?.Trim();
        // Successful commitment retries validate their original snapshot, not a later amendment.
        if (!amendment && row.State == "Ordered")
        {
            var original = await Revisions(database, id).SingleAsync(r => r.Revision == 1, cancellationToken);
            draft = JsonSerializer.Deserialize<DraftContent>(original.DraftJson, DraftOrderInput.JsonOptions)!;
        }
        errors = PurchaseOrderInput.Validate(draft, orderDate, reason, amendment);
        if (errors.Count > 0) return Validation(errors);
        if (amendment && Convert.ToBase64String(row.RowVersion) == expected)
        {
            errors = DraftOrderInput.ValidateConfirmedCorrections(Content(row).Charges, draft!.Charges, row.SupplierId != draft.SupplierId || row.SupplierName != draft.SupplierName);
            if (errors.Count > 0) return Validation(errors);
        }
        await database.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = new SqlCommand("Purchasing.SavePurchaseOrder", (SqlConnection)database.Database.GetDbConnection()) { CommandType = CommandType.StoredProcedure };
            command.Parameters.AddWithValue("@RequestId", requestId); command.Parameters.AddWithValue("@ActorUserId", actor.UserId); command.Parameters.AddWithValue("@TargetId", id);
            command.Parameters.Add(new("@ExpectedVersion", SqlDbType.VarBinary, -1) { Value = Convert.FromBase64String(expected!) });
            command.Parameters.AddWithValue("@Operation", amendment ? "Amend" : "Commit");
            command.Parameters.Add(new("@OrderDate", SqlDbType.NVarChar, -1) { Value = orderDate });
            command.Parameters.Add(new("@Reason", SqlDbType.NVarChar, -1) { Value = (object?)reason ?? DBNull.Value });
            command.Parameters.Add(new("@Draft", SqlDbType.NVarChar, -1) { Value = amendment ? JsonSerializer.Serialize(draft, DraftOrderInput.JsonOptions) : DBNull.Value });
            command.Parameters.Add(new("@Calculation", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(DraftOrderInput.Calculate(draft!), DraftOrderInput.JsonOptions) });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Purchase command returned no receipt.");
            return Results.Ok(new SavePurchaseOrderResponse(reader.GetGuid(reader.GetOrdinal("RequestId")), reader.GetBoolean(reader.GetOrdinal("Replayed")), reader.GetGuid(reader.GetOrdinal("DraftOrderId")), Convert.ToBase64String((byte[])reader["SavedVersion"]), DraftOrderCursor.Timestamp(reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("CompletedAtUtc"))), reader.GetInt32(reader.GetOrdinal("Revision"))));
        }
        catch (SqlException ex) when (ex.Number is >= 50400 and <= 50417)
        {
            return ex.Number switch
            {
                50400 => Validation(new() { ["draft"] = ["Review the purchase fields and confirmed charge corrections."] }),
                50401 or 50416 => Validation(new() { ["draft.currency"] = ["Currency is fixed after commitment."] }),
                50417 => Validation(new() { ["draft"] = ["An amendment must change the saved purchase."] }),
                50403 => Problem(403, "purchase_authority_required", "Current business authority is required."),
                50404 or 50414 => Problem(404, "purchase_not_found", "Purchase or supplier not found."),
                50409 => Problem(409, "purchase_version_conflict", "The purchase changed. Review its current version."),
                50410 => Problem(409, "purchase_request_conflict", "This request identifier was already used for different input."),
                _ => Problem(409, "purchase_state_conflict", "The purchase state or supplier selection changed. Reload to review it.")
            };
        }
        finally { await database.Database.CloseConnectionAsync(); }
    }
    private static IQueryable<RevisionRow> Revisions(WorkbenchDbContext database, Guid id) => database.Database.SqlQuery<RevisionRow>($"SELECT Revision,OrderDate,ActorUserId,RecordedAtUtc,Reason,CalculationPolicyVersion,DraftJson,CalculationJson,PoReference FROM Purchasing.PurchaseOrderRevisions WHERE DraftOrderId={id}");
    private static async Task<IResult> HistoryAsync(Guid id, string? cursor, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        if (!await database.DraftOrders.AnyAsync(r => r.Id == id && !r.IsDeleted && r.State == "Ordered", cancellationToken)) return Problem(404, "purchase_not_found", "Ordered purchase not found.");
        var before = int.MaxValue;
        if (cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out before) || before <= 0)) return Problem(400, "invalid_cursor", "Refresh purchase history.");
        var rows = await Revisions(database, id).Where(r => r.Revision < before).OrderByDescending(r => r.Revision).Take(51).ToListAsync(cancellationToken);
        return Results.Ok(new PurchaseOrderRevisionPageResponse(rows.Take(50).Select(r => new PurchaseOrderRevisionSummary(r.Revision, r.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), r.ActorUserId, DraftOrderCursor.Timestamp(r.RecordedAtUtc), r.Reason, r.CalculationPolicyVersion)).ToArray(), rows.Count > 50 ? rows[49].Revision.ToString(CultureInfo.InvariantCulture) : null));
    }
    private static async Task<IResult> RevisionAsync(Guid id, int revision, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        var row = await Revisions(database, id).SingleOrDefaultAsync(r => r.Revision == revision, cancellationToken);
        return row is null ? Problem(404, "purchase_revision_not_found", "Revision not found.") : Results.Ok(new PurchaseOrderRevisionResponse(row.Revision, row.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), row.ActorUserId, DraftOrderCursor.Timestamp(row.RecordedAtUtc), row.Reason, row.CalculationPolicyVersion, JsonSerializer.Deserialize<DraftContent>(row.DraftJson, DraftOrderInput.JsonOptions)!, JsonSerializer.Deserialize<DraftCalculationResponse>(row.CalculationJson, DraftOrderInput.JsonOptions)!, row.PoReference));
    }
    private sealed record RevisionRow(int Revision, DateOnly OrderDate, Guid ActorUserId, DateTimeOffset RecordedAtUtc, string? Reason, int CalculationPolicyVersion, string DraftJson, string CalculationJson, string PoReference);
    private static IResult Validation(Dictionary<string, string[]> errors) => Results.ValidationProblem(errors, title: "Review the purchase fields.", extensions: new Dictionary<string, object?> { ["code"] = "purchase_validation_failed" });
    private static IResult Problem(int status, string code, string title) => Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
