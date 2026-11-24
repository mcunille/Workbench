// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Persistence;
using Workbench.Server.Purchasing;
using static Workbench.Server.Accounting.SupplierReconciliationQueries;

namespace Workbench.Server.Accounting;

internal static class SupplierFinancialSourceReports
{
    internal static bool IsTypedSource(string kind) =>
        kind is "SupplierCredit" or "SupplierRefund" or "SupplierRefundRepayment" or "SupplierRefundRelease";

    internal static SupplierFinancialSourceEvidence? Read(Snapshot snapshot, string kind, Guid sourceId, Guid revision)
    {
        if (kind == "SupplierCredit")
        {
            var credit = snapshot.Evidence(snapshot.CreditsById, sourceId).SingleOrDefault();
            if (credit is null || credit.RevisionId != revision || credit.PostingDate > snapshot.Filter.PostingThrough ||
                !snapshot.HasVerifiedCredit(sourceId)) return null;
            var claims = snapshot.Evidence(snapshot.ClaimsByCredit, sourceId).OrderBy(row => row.Id)
                .Select(row => new SupplierCreditClaimEvidence(row.Id, row.BillId, row.BillRevisionId, row.RecognitionEventId,
                    row.ComponentKey, Money(Units(row.Amount), snapshot.Scale), row.Treatment, row.AccountId, row.AccountVersion)).ToArray();
            return new(kind, sourceId, revision, Money(Units(credit.Total), snapshot.Scale), credit.DocumentDate,
                credit.EffectiveDate, credit.PostingDate, null, null, credit.Reference, null, null, claims,
                [], Lineage(snapshot, kind, sourceId, revision));
        }

        if (kind == "SupplierRefundRelease")
        {
            var release = snapshot.Evidence(snapshot.ReleasesBySettlement, sourceId).SingleOrDefault();
            var settlement = snapshot.Evidence(snapshot.SettlementsById, sourceId).SingleOrDefault();
            if (release is null || settlement is null || release.CorrectionId != revision || !VisibleRelease(snapshot, release))
                return null;
            var original = snapshot.Evidence(snapshot.CashById, settlement.CashSourceId).SingleOrDefault();
            return original is null ? null : Read(snapshot, original.Kind, original.Id, original.RevisionId);
        }

        if (kind is not ("SupplierRefund" or "SupplierRefundRepayment")) return null;
        var cash = snapshot.Evidence(snapshot.CashById, sourceId).SingleOrDefault();
        if (cash is null || cash.Kind != kind || cash.RevisionId != revision || cash.PostingDate > snapshot.Filter.PostingThrough ||
            !snapshot.HasVerifiedCash(cash)) return null;
        return new(kind, sourceId, revision, Money(Units(cash.Amount), snapshot.Scale), cash.ActualDate,
            cash.EffectiveDate, cash.PostingDate, cash.ActualDate, cash.Method, cash.Reference, cash.FundingAccountId,
            cash.FundingAccountVersion, [], Settlements(snapshot, snapshot.Evidence(snapshot.SettlementsByCash, sourceId)),
            Lineage(snapshot, kind, sourceId, revision));
    }

    internal static async Task<SupplierFinancialSourceEvidence?> ReadJournalAsync(WorkbenchDbContext db, Guid journalId,
        CancellationToken ct)
    {
        var recorded = await db.Database.SqlQuery<DateTimeOffset>($"SELECT CONVERT(datetimeoffset,SYSUTCDATETIME()) AS Value").SingleAsync(ct);
        var groupCeiling = await db.SupplierFinancialGroups.MaxAsync(row => (long?)row.Sequence, ct) ?? 0;
        var journalCeiling = await db.JournalEntries.MaxAsync(row => (long?)row.Sequence, ct) ?? 0;
        var filter = new SupplierReportFilters(db.TenantContext.RequireTenantId(), "journal", null, null, null,
            DateOnly.MaxValue, recorded, 1, groupCeiling, journalCeiling);
        var snapshot = await Snapshot.Load(db, filter, ct);
        snapshot.IndexEvidence();
        if (!snapshot.Journals.TryGetValue(journalId, out var journal)) return null;
        var source = OriginalSource(snapshot, journal.SourceEventId);
        return source is null || !snapshot.HasVerifiedSourceEvent(source)
            ? null : Read(snapshot, source.SourceKind, source.SourceId, source.SourceRevision);
    }

    internal static (IReadOnlyList<SupplierCashSettlementEvidence> Settlements, IReadOnlyList<SupplierSourceCorrectionEvidence> Corrections)
        ReadHistory(Snapshot snapshot, SupplierItemMovement movement, SupplierOpenItem item)
    {
        var source = OriginalSource(snapshot, movement.SourceEventId);
        IEnumerable<SupplierCashSettlement> settlements = source?.SourceKind switch
        {
            "SupplierRefund" or "SupplierRefundRepayment" => snapshot.Evidence(snapshot.SettlementsByCash, source.SourceId)
                .Where(row => row.ItemId == movement.ItemId),
            "SupplierRefundRelease" => snapshot.Evidence(snapshot.SettlementsById, source.SourceId),
            _ => []
        };
        var corrections = snapshot.Evidence(snapshot.SourceCorrectionsByGroup, movement.GroupId);
        if (source is not null)
            corrections = corrections.Concat(snapshot.Evidence(snapshot.CorrectionsByOriginal,
                (source.SourceKind, source.SourceId, source.SourceRevision)));
        if (item.SourceKind is "SupplierBill" or "SupplierCredit")
            corrections = corrections.Concat(snapshot.Evidence(snapshot.CorrectionsByOriginal,
                (item.SourceKind, item.SourceId, item.SourceRevisionId)));
        return (Settlements(snapshot, settlements), CorrectionRows(snapshot, corrections));
    }

    private static JournalSourceEvent? OriginalSource(Snapshot snapshot, Guid sourceEventId)
    {
        var inverse = snapshot.Evidence(snapshot.CorrectionsByReversalSource, sourceEventId).SingleOrDefault();
        return snapshot.Sources.GetValueOrDefault(inverse?.OriginalSourceEventId ?? sourceEventId);
    }

    private static bool VisibleRelease(Snapshot snapshot, SupplierCashSettlementRelease row) =>
        row.PostingDate <= snapshot.Filter.PostingThrough && snapshot.HasVerifiedRelease(row.SettlementId) &&
        snapshot.Journals.TryGetValue(row.JournalId, out var journal) && snapshot.Visible(journal);

    private static SupplierCashSettlementEvidence[] Settlements(Snapshot snapshot, IEnumerable<SupplierCashSettlement> rows)
    {
        var result = new List<SupplierCashSettlementEvidence>();
        foreach (var row in snapshot.Observe(rows))
        {
            if (!snapshot.Journals.TryGetValue(row.JournalId, out var journal) || !snapshot.Visible(journal) ||
                !snapshot.Sources.TryGetValue(journal.SourceEventId, out var source) || snapshot.CashOwner(source)?.JournalId != row.JournalId)
                continue;
            var release = snapshot.Evidence(snapshot.ReleasesBySettlement, row.Id)
                .SingleOrDefault(candidate => VisibleRelease(snapshot, candidate));
            result.Add(new(row.Id, row.CashSourceId, row.ItemId, Money(Units(row.Amount), snapshot.Scale),
                journal.PostingDate, journal.RecordedAtUtc, release?.GroupId));
        }
        return result.OrderBy(row => row.PostingDate).ThenBy(row => row.Id).ToArray();
    }

    private static SupplierSourceCorrectionEvidence[] Lineage(Snapshot snapshot, string kind, Guid id, Guid revision) =>
        CorrectionRows(snapshot, snapshot.Evidence(snapshot.CorrectionsByOriginal, (kind, id, revision))
            .Concat(snapshot.Evidence(snapshot.CorrectionsByReplacement, (kind, id, revision))));

    private static SupplierSourceCorrectionEvidence[] CorrectionRows(Snapshot snapshot, IEnumerable<SupplierSourceCorrection> rows) =>
        snapshot.Observe(rows).Where(snapshot.VisibleSourceCorrection).DistinctBy(row => row.Id)
            .OrderBy(row => row.RecordedAtUtc).ThenBy(row => row.Id)
            .Select(row => new SupplierSourceCorrectionEvidence(row.Id, row.SourceKind, row.SourceId, row.ReplacementSourceId,
                row.GroupId, row.Reason, row.PostingDate, row.RecordedAtUtc)).ToArray();
}
