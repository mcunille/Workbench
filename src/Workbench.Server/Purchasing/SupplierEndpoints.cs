// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Authorization;
using Workbench.Server.Http;
using Workbench.Server.Persistence;
namespace Workbench.Server.Purchasing;

public static class SupplierEndpoints
{
    public static void MapSuppliers(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/suppliers").WithTags("Purchasing").RequireAuthorization();
        group.MapGet("", BrowseAsync).Produces<SupplierPageResponse>().ProducesProblem(400);
        group.MapGet("/{id:guid}", ReadAsync).Produces<SupplierResponse>().ProducesProblem(404);
        group.MapPost("", CreateAsync).WithMetadata(WorkbenchAntiforgeryMetadata.Instance).Produces<SaveSupplierResponse>(201).Produces<SaveSupplierResponse>().ProducesValidationProblem().ProducesProblem(409).ProducesProblem(413);
        group.MapPut("/{id:guid}", UpdateAsync).WithMetadata(WorkbenchAntiforgeryMetadata.Instance).Produces<SaveSupplierResponse>().ProducesValidationProblem().ProducesProblem(404).ProducesProblem(409).ProducesProblem(413);
        group.MapPost("/{id:guid}/archive", ArchiveAsync).WithMetadata(WorkbenchAntiforgeryMetadata.Instance).Produces<SaveSupplierResponse>().ProducesValidationProblem().ProducesProblem(404).ProducesProblem(409).ProducesProblem(413);
    }
    private static SupplierResponse Response(Supplier row) => new(row.Id, new(row.Name, row.ContactName, row.Email, row.Phone, row.Website, row.PostalAddress), row.IsArchived, DraftOrderCursor.Timestamp(row.CreatedAtUtc), DraftOrderCursor.Timestamp(row.UpdatedAtUtc), Convert.ToBase64String(row.RowVersion));
    private static async Task<IResult> BrowseAsync(string? cursor, string? query, bool? includeArchived, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        query = PurchasingIdentityInput.Query(query);
        var binding = database.TenantContext.RequireTenantId().ToString("N") + ":" + (includeArchived == true ? "archived:" : "active:") + query;
        if (query?.Length > 200 || query?.Any(char.IsControl) == true) return Problem(400, "invalid_query", "Use up to 200 characters for search.");
        if (!PurchasingIdentityInput.Decode(cursor, binding, out var timestamp, out var id)) return Problem(400, "invalid_cursor", "Refresh suppliers to start a new page.");
        var rows = await database.Suppliers.FromSql($"SELECT * FROM [Purchasing].[Suppliers] WHERE ({includeArchived == true}=1 OR IsArchived=0) AND ({cursor} IS NULL OR UpdatedAtUtc<{timestamp} OR (UpdatedAtUtc={timestamp} AND Id<{id})) AND ({query} IS NULL OR CHARINDEX({query},UPPER(Name) COLLATE Latin1_General_100_CI_AS)>0)").AsNoTracking().OrderByDescending(row => row.UpdatedAtUtc).ThenByDescending(row => row.Id).Take(51).ToListAsync(cancellationToken);
        return Results.Ok(new SupplierPageResponse(rows.Take(50).Select(Response).ToArray(), rows.Count > 50 ? PurchasingIdentityInput.Cursor(rows[49].UpdatedAtUtc, rows[49].Id, binding) : null));
    }
    private static async Task<IResult> ReadAsync(Guid id, WorkbenchDbContext database, CancellationToken cancellationToken)
    {
        var row = await database.Suppliers.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
        return row is null ? Problem(404, "supplier_not_found", "Supplier not found.") : Results.Ok(Response(row));
    }
    private static Task<IResult> CreateAsync(CreateSupplierRequest request, WorkbenchDbContext database, RequestActor actor, CancellationToken cancellationToken) => SaveAsync(request.RequestId, null, null, request.Supplier, null, database, actor, cancellationToken);
    private static Task<IResult> UpdateAsync(Guid id, UpdateSupplierRequest request, WorkbenchDbContext database, RequestActor actor, CancellationToken cancellationToken) => SaveAsync(request.RequestId, id, request.ExpectedVersion, request.Supplier, null, database, actor, cancellationToken);
    private static Task<IResult> ArchiveAsync(Guid id, ArchiveSupplierRequest request, WorkbenchDbContext database, RequestActor actor, CancellationToken cancellationToken) => SaveAsync(request.RequestId, id, request.ExpectedVersion, null, request.IsArchived, database, actor, cancellationToken);
    private static async Task<IResult> SaveAsync(Guid requestId, Guid? id, string? version, SupplierContent? input, bool? isArchived, WorkbenchDbContext database, RequestActor actor, CancellationToken cancellationToken)
    {
        var supplier = input is null ? null : PurchasingIdentityInput.Normalize(input);
        var errors = isArchived is null ? PurchasingIdentityInput.Validate(supplier) : new Dictionary<string, string[]>();
        if (requestId == Guid.Empty) errors["requestId"] = ["A nonempty request identifier is required."];
        var expectedVersion = id is null ? null : DraftOrderInput.NormalizeVersion(version, errors);
        if (errors.Count > 0) return Validation(errors);
        var operation = isArchived is not null ? "Archive" : id is null ? "Create" : "Update";
        await database.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = new SqlCommand("[Purchasing].[SaveSupplier]", (SqlConnection)database.Database.GetDbConnection()) { CommandType = CommandType.StoredProcedure };
            command.Parameters.Add(new SqlParameter("@RequestId", SqlDbType.UniqueIdentifier) { Value = requestId });
            command.Parameters.Add(new SqlParameter("@ActorUserId", SqlDbType.UniqueIdentifier) { Value = actor.UserId });
            command.Parameters.Add(new SqlParameter("@CanonicalInputJson", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(new { operation, targetId = id, expectedVersion, supplier, isArchived }, DraftOrderInput.JsonOptions) });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Supplier save returned no receipt.");
            var response = new SaveSupplierResponse(reader.GetGuid(reader.GetOrdinal("RequestId")), reader.GetBoolean(reader.GetOrdinal("Replayed")), reader.GetGuid(reader.GetOrdinal("SupplierId")), Convert.ToBase64String((byte[])reader["SavedVersion"]), DraftOrderCursor.Timestamp(reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("CompletedAtUtc"))));
            return id is null && !response.Replayed ? Results.Created($"/api/suppliers/{response.SupplierId:D}", response) : Results.Ok(response);
        }
        catch (SqlException exception) when (exception.Number is 50500 or 50503 or 50504 or 50509 or 50510)
        {
            return exception.Number switch
            {
                50500 => Validation(new() { ["supplier"] = ["Review the supplier fields."] }),
                50503 => Problem(403, "supplier_authority_required", "Current business authority is required."),
                50504 => Problem(404, "supplier_not_found", "Supplier not found."),
                50509 => Problem(409, "supplier_version_conflict", "The supplier changed. Review the saved details."),
                _ => Problem(409, "supplier_request_conflict", "This request identifier was used for different input.")
            };
        }
        finally { await database.Database.CloseConnectionAsync(); }
    }
    private static IResult Validation(Dictionary<string, string[]> errors) => Results.ValidationProblem(errors, title: "Review supplier details.", extensions: new Dictionary<string, object?> { ["code"] = "supplier_validation_failed" });
    private static IResult Problem(int status, string code, string title) => Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
