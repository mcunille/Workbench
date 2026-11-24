// Copyright (c) 2026 The White Stag Collection.
using Microsoft.AspNetCore.DataProtection;
using Workbench.Server.Persistence;
using Workbench.Server.Purchasing;
using static Workbench.Server.Accounting.SupplierReconciliationQueries;

namespace Workbench.Server.Accounting;

internal static class SupplierOpenItemQueries
{
    internal static Task<IResult> Browse(HttpContext http, WorkbenchDbContext db, IDataProtectionProvider protection, CancellationToken ct) =>
        Run(http, db, protection, "items", snapshot =>
        {
            var page = snapshot.Page(snapshot.SelectedItems.Select(i => Summary(snapshot, i)).ToList(), i => i.Id.ToString("D"),
                i => (i.Kind, Units(i.Balance)), protection);
            return Task.FromResult<IResult>(Results.Ok(page with
            {
                Items = page.Items.Select(i => i with { SupplierSource = SourceEvidence(snapshot, i) }).ToArray()
            }));
        }, ct);

    internal static Task<IResult> Read(Guid id, HttpContext http, WorkbenchDbContext db, IDataProtectionProvider protection, CancellationToken ct) =>
        Run(http, db, protection, $"item:{id:D}", async snapshot =>
        {
            var item = snapshot.SelectedItems.SingleOrDefault(i => i.Id == id);
            if (item is null) return JournalReportEndpoints.Unavailable();
            var actor = http.RequestServices.GetRequiredService<Authorization.RequestActor>();
            var evidence = await FinancialEvidenceQueries.ReadAsync(db, actor, item.SourceKind, item.SourceId, item.SourceRevisionId, ct);
            var summary = Summary(snapshot, item);
            return Results.Ok(summary with { FinancialEvidence = evidence, SupplierSource = SourceEvidence(snapshot, summary) });
        }, ct);

    internal static Task<IResult> History(Guid id, HttpContext http, WorkbenchDbContext db, IDataProtectionProvider protection, CancellationToken ct) =>
        Run(http, db, protection, $"history:{id:D}", snapshot =>
        {
            var item = snapshot.SelectedItems.SingleOrDefault(i => i.Id == id);
            if (item is null) return Task.FromResult(JournalReportEndpoints.Unavailable());
            var page = snapshot.Page(snapshot.Evidence(snapshot.MovementsByItem, id).Where(snapshot.Visible).ToList(),
                m => $"{snapshot.Groups[m.GroupId].Sequence:D20}:{m.Id:D}", m => (item.Kind, Units(m.Amount)), protection);
            var rows = page.Items.Select(m =>
            {
                var journal = snapshot.Evidence(snapshot.JournalsBySource, m.SourceEventId).SingleOrDefault();
                var correction = snapshot.Evidence(snapshot.CorrectionsByReversalSource, m.SourceEventId).SingleOrDefault();
                var source = snapshot.Sources.GetValueOrDefault(correction?.OriginalSourceEventId ?? m.SourceEventId);
                var application = source is { SourceKind: "SupplierApplication", EventKind: "Apply" }
                    ? snapshot.Evidence(snapshot.ApplicationsById, source.SourceId).SingleOrDefault(a => a.GroupId == source.SourceRevision)
                    : null;
                var reversal = source is { SourceKind: "SupplierApplicationReversal", EventKind: "Reverse" }
                    ? snapshot.Evidence(snapshot.ReversalsById, source.SourceId).SingleOrDefault(r => r.GroupId == source.SourceRevision)
                    : null;
                var applications = snapshot.Evidence(snapshot.ApplicationsById, application?.Id ?? reversal?.ApplicationId)
                    .Where(a => a.FundingItemId == id || a.DebtItemId == id)
                    .Concat(source?.SourceKind == "SupplierPayment"
                        ? snapshot.Evidence(snapshot.ApplicationsByFunding, source.SourceId).Where(a =>
                            (a.FundingItemId == id || a.DebtItemId == id) && snapshot.Evidence(snapshot.MovementsByGroupSource,
                                (a.GroupId, correction?.OriginalSourceEventId ?? m.SourceEventId)).Any())
                        : []);
                var evidence = SupplierFinancialSourceReports.ReadHistory(snapshot, m, item);
                return new SupplierItemHistoryEntry(m.Id, m.ItemId, m.GroupId, snapshot.Groups[m.GroupId].Sequence,
                    m.EventKind, m.SourceEventId, journal?.Id,
                    applications.Select(a => a.Id).Order().ToArray(), correction?.Id,
                    snapshot.Evidence(snapshot.PaymentCorrectionsByGroup, m.GroupId).SingleOrDefault()?.Id,
                    m.PostingDate, snapshot.Groups[m.GroupId].RecordedAtUtc, Money(Units(m.Amount), snapshot.Scale),
                    evidence.Settlements, evidence.Corrections);
            }).ToArray();
            return Task.FromResult<IResult>(Results.Ok(new SupplierReportPage<SupplierItemHistoryEntry>(rows, page.NextCursor,
                page.PostingThrough, page.RecordedThrough, page.WholeFilterTotals, page.PageTotals)));
        }, ct);

    private static SupplierFinancialSourceEvidence? SourceEvidence(Snapshot snapshot, SupplierOpenItemSummary item) =>
        SupplierFinancialSourceReports.Read(snapshot, item.SourceKind, item.SourceId, item.SourceRevisionId);

    private static SupplierOpenItemSummary Summary(Snapshot snapshot, SupplierOpenItem item) => new(item.Id, item.Kind,
        item.SupplierId, item.PurchaseOrderId, item.BillId, item.Currency, item.SourceKind, item.SourceId, item.SourceRevisionId,
        item.SourcePostingDate, item.DueDate, Money(snapshot.Balance(item.Id), snapshot.Scale), snapshot.ValidSource(item),
        snapshot.Filter.PostingThrough, snapshot.Filter.RecordedThrough!.Value);
}
