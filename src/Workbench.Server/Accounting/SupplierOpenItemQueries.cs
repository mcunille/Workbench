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
        Run(http, db, protection, $"item:{id:D}", snapshot =>
        {
            var item = snapshot.SelectedItems.SingleOrDefault(i => i.Id == id);
            return Task.FromResult(item is null ? JournalReportEndpoints.Unavailable() : Results.Ok(Summary(snapshot, item)));
        }, ct);

    internal static Task<IResult> History(Guid id, HttpContext http, WorkbenchDbContext db, IDataProtectionProvider protection, CancellationToken ct) =>
        Run(http, db, protection, $"history:{id:D}", snapshot =>
        {
            var item = snapshot.SelectedItems.SingleOrDefault(i => i.Id == id);
            if (item is null) return Task.FromResult(JournalReportEndpoints.Unavailable());
            var rows = snapshot.Movements.Where(m => m.ItemId == id && snapshot.Visible(m)).Select(m =>
            {
                var journal = snapshot.Journals.Values.SingleOrDefault(j => j.SourceEventId == m.SourceEventId);
                var source = snapshot.Sources.GetValueOrDefault(m.SourceEventId);
                var application = snapshot.Applications.SingleOrDefault(a => a.Id == source?.SourceId);
                var reversal = snapshot.Reversals.SingleOrDefault(r => r.Id == source?.SourceId);
                var correction = snapshot.Corrections.SingleOrDefault(c => c.ReversalSourceEventId == m.SourceEventId);
                return new SupplierItemHistoryEntry(m.Id, m.ItemId, m.GroupId, snapshot.Groups[m.GroupId].Sequence,
                    m.EventKind, m.SourceEventId, journal?.Id,
                    snapshot.Applications.Where(a => a.Id == application?.Id || a.Id == reversal?.ApplicationId ||
                        (source?.SourceKind == "SupplierPayment" && a.FundingItemId == source.SourceId && (a.FundingItemId == id || a.DebtItemId == id) &&
                         snapshot.Movements.Any(original => original.GroupId == a.GroupId && original.SourceEventId == (correction?.OriginalSourceEventId ?? m.SourceEventId))))
                        .Select(a => a.Id).Order().ToArray(), correction?.Id, snapshot.PaymentCorrections.SingleOrDefault(p => p.GroupId == m.GroupId)?.Id,
                    m.PostingDate, snapshot.Groups[m.GroupId].RecordedAtUtc, Money(Units(m.Amount), snapshot.Scale));
            }).ToList();
            return Task.FromResult<IResult>(Results.Ok(snapshot.Page(rows, r => $"{r.GroupSequence:D20}:{r.Id:D}",
                r => (item.Kind, Units(r.Amount)), protection)));
        }, ct);

    private static SupplierOpenItemSummary Summary(Snapshot snapshot, SupplierOpenItem item) => new(item.Id, item.Kind,
        item.SupplierId, item.PurchaseOrderId, item.BillId, item.Currency, item.SourceKind, item.SourceId, item.SourceRevisionId,
        item.SourcePostingDate, item.DueDate, Money(snapshot.Balance(item.Id), snapshot.Scale), snapshot.ValidSource(item), snapshot.Filter.PostingThrough, snapshot.Filter.RecordedThrough!.Value);
}
