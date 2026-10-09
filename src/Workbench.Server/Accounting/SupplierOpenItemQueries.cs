// Copyright (c) 2026 The White Stag Collection.
using Microsoft.AspNetCore.DataProtection;
using Workbench.Server.Persistence;
using Workbench.Server.Purchasing;
using static Workbench.Server.Accounting.SupplierReconciliationQueries;

namespace Workbench.Server.Accounting;

internal static class SupplierOpenItemQueries
{
    internal static Task<IResult> Browse(HttpContext http, WorkbenchDbContext db, IDataProtectionProvider protection, CancellationToken ct) =>
        Run(http, db, protection, "items", snapshot => Task.FromResult<IResult>(Results.Ok(snapshot.Page(
            snapshot.SelectedItems.Select(i => Summary(snapshot, i)).ToList(), i => i.Id.ToString("D"),
            i => (i.Kind, Units(i.Balance)), protection))), ct);

    internal static Task<IResult> Read(Guid id, HttpContext http, WorkbenchDbContext db, IDataProtectionProvider protection, CancellationToken ct) =>
        Run(http, db, protection, $"item:{id:D}", async snapshot =>
        {
            var item = snapshot.SelectedItems.SingleOrDefault(i => i.Id == id);
            if (item is null) return JournalReportEndpoints.Unavailable();
            var actor = http.RequestServices.GetRequiredService<Authorization.RequestActor>();
            var evidence = await FinancialEvidenceQueries.ReadAsync(db, actor, item.SourceId, item.SourceRevisionId, ct);
            return Results.Ok(Summary(snapshot, item) with { FinancialEvidence = evidence });
        }, ct);

    internal static Task<IResult> History(Guid id, HttpContext http, WorkbenchDbContext db, IDataProtectionProvider protection, CancellationToken ct) =>
        Run(http, db, protection, $"history:{id:D}", snapshot =>
        {
            var item = snapshot.SelectedItems.SingleOrDefault(i => i.Id == id);
            if (item is null) return Task.FromResult(JournalReportEndpoints.Unavailable());
            var rows = snapshot.Evidence(snapshot.MovementsByItem, id).Where(snapshot.Visible).Select(m =>
            {
                var journal = snapshot.Evidence(snapshot.JournalsBySource, m.SourceEventId).SingleOrDefault();
                var source = snapshot.Sources.GetValueOrDefault(m.SourceEventId);
                var application = snapshot.Evidence(snapshot.ApplicationsById, source?.SourceId).SingleOrDefault();
                var reversal = snapshot.Evidence(snapshot.ReversalsById, source?.SourceId).SingleOrDefault();
                var correction = snapshot.Evidence(snapshot.CorrectionsByReversalSource, m.SourceEventId).SingleOrDefault();
                var applications = snapshot.Evidence(snapshot.ApplicationsById, application?.Id)
                    .Concat(snapshot.Evidence(snapshot.ApplicationsById, reversal?.ApplicationId))
                    .Concat(source?.SourceKind == "SupplierPayment"
                        ? snapshot.Evidence(snapshot.ApplicationsByFunding, source.SourceId).Where(a =>
                            (a.FundingItemId == id || a.DebtItemId == id) && snapshot.Evidence(snapshot.MovementsByGroupSource,
                                (a.GroupId, correction?.OriginalSourceEventId ?? m.SourceEventId)).Any())
                        : [])
                    // One application can match multiple branches of the original ownership predicate.
                    .Distinct();
                return new SupplierItemHistoryEntry(m.Id, m.ItemId, m.GroupId, snapshot.Groups[m.GroupId].Sequence,
                    m.EventKind, m.SourceEventId, journal?.Id,
                    applications.Select(a => a.Id).Order().ToArray(), correction?.Id,
                    snapshot.Evidence(snapshot.PaymentCorrectionsByGroup, m.GroupId).SingleOrDefault()?.Id,
                    m.PostingDate, snapshot.Groups[m.GroupId].RecordedAtUtc, Money(Units(m.Amount), snapshot.Scale));
            }).ToList();
            return Task.FromResult<IResult>(Results.Ok(snapshot.Page(rows, r => $"{r.GroupSequence:D20}:{r.Id:D}",
                r => (item.Kind, Units(r.Amount)), protection)));
        }, ct);

    private static SupplierOpenItemSummary Summary(Snapshot snapshot, SupplierOpenItem item) => new(item.Id, item.Kind,
        item.SupplierId, item.PurchaseOrderId, item.BillId, item.Currency, item.SourceKind, item.SourceId, item.SourceRevisionId,
        item.SourcePostingDate, item.DueDate, Money(snapshot.Balance(item.Id), snapshot.Scale), snapshot.ValidSource(item), snapshot.Filter.PostingThrough, snapshot.Filter.RecordedThrough!.Value);
}
