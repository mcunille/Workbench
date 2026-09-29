// Copyright (c) 2026 The White Stag Collection.
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Workbench.Server.Persistence;

namespace Workbench.Server.Accounting;

public static class SupplierOpenItemEndpoints
{
    public static void MapSupplierOpenItemReports(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/beta/accounting").WithTags("Accounting reports")
            .RequireAuthorization("AccountingReportsRead");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            return await next(context);
        });
        group.MapGet("/supplier-open-items", Browse).Produces<SupplierReportPage<SupplierOpenItemSummary>>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(422);
        group.MapGet("/supplier-open-items/{id:guid}", Read).Produces<SupplierOpenItemSummary>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(422);
        group.MapGet("/supplier-open-items/{id:guid}/history", History).Produces<SupplierReportPage<SupplierItemHistoryEntry>>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(422);
        group.MapGet("/supplier-reconciliation", Reconcile).Produces<SupplierReconciliationSummary>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(422);
    }

    private static Task<IResult> Browse(HttpContext http, [AsParameters] SupplierReportParameters parameters,
        WorkbenchDbContext db, IDataProtectionProvider protection, CancellationToken ct) => SupplierOpenItemQueries.Browse(http, db, protection, ct);
    private static Task<IResult> Read(Guid id, HttpContext http, [AsParameters] SupplierReportParameters parameters,
        WorkbenchDbContext db, IDataProtectionProvider protection, CancellationToken ct) => SupplierOpenItemQueries.Read(id, http, db, protection, ct);
    private static Task<IResult> History(Guid id, HttpContext http, [AsParameters] SupplierReportParameters parameters,
        WorkbenchDbContext db, IDataProtectionProvider protection, CancellationToken ct) => SupplierOpenItemQueries.History(id, http, db, protection, ct);
    private static Task<IResult> Reconcile(HttpContext http, [AsParameters] SupplierReportParameters parameters,
        WorkbenchDbContext db, IDataProtectionProvider protection, CancellationToken ct) => SupplierReconciliationQueries.Reconcile(http, db, protection, ct);
}

// Declare the shared query contract for OpenAPI; strict validation remains centralized.
public sealed class SupplierReportParameters
{
    [FromQuery(Name = "supplierId")] public string? SupplierId { get; set; }
    [FromQuery(Name = "purchaseOrderId")] public string? PurchaseOrderId { get; set; }
    [FromQuery(Name = "billId")] public string? BillId { get; set; }
    [FromQuery(Name = "postingThrough")] public string? PostingThrough { get; set; }
    [FromQuery(Name = "recordedThrough")] public string? RecordedThrough { get; set; }
    [FromQuery(Name = "pageSize")] public int? PageSize { get; set; }
    [FromQuery(Name = "cursor")] public string? Cursor { get; set; }
}
