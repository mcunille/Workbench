// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Globalization;
using System.Numerics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Workbench.Server.Persistence;
using Workbench.Server.Purchasing;

namespace Workbench.Server.Accounting;

internal static partial class SupplierReconciliationQueries
{
    internal sealed partial class Snapshot(SupplierReportFilters filter, CancellationToken cancellation)
    {
        internal SupplierReportFilters Filter { get; } = filter;
        internal int Scale { get; private set; } = 2;
        internal List<SupplierOpenItem> Items { get; private set; } = [];
        internal Dictionary<Guid, SupplierFinancialGroup> Groups { get; private set; } = [];
        internal List<SupplierItemMovement> Movements { get; private set; } = [];
        internal List<SupplierControlAttribution> Attributions { get; private set; } = [];
        internal Dictionary<Guid, JournalEntry> Journals { get; private set; } = [];
        internal Dictionary<Guid, JournalSourceEvent> Sources { get; private set; } = [];
        internal List<JournalLineRow> Lines { get; private set; } = [];
        internal List<ControlEvidence> Controls { get; private set; } = [];
        internal List<SupplierApplication> Applications { get; private set; } = [];
        internal List<SupplierApplicationReversal> Reversals { get; private set; } = [];
        internal List<SupplierPaymentCorrection> PaymentCorrections { get; private set; } = [];
        internal List<JournalCorrectionGroup> Corrections { get; private set; } = [];
        internal List<RecognitionSideEvent> Recognition { get; private set; } = [];
        internal List<RecognitionUnit> RecognitionUnits { get; private set; } = [];
        internal List<RecognitionOwner> RecognitionOwners { get; private set; } = [];
        internal List<RecognitionEventCorrection> RecognitionCorrections { get; private set; } = [];
        internal int UnknownCount { get; private set; }

        // Lookups retain every row: duplicate attribution is evidence, not a value to overwrite.
        internal ILookup<Guid, SupplierItemMovement> MovementsByItem { get; private set; } = null!;
        internal ILookup<(Guid Group, Guid Source), SupplierItemMovement> MovementsByGroupSource { get; private set; } = null!;
        private ILookup<Guid, SupplierItemMovement> movementsById = null!;
        private ILookup<(Guid Source, Guid Item), SupplierItemMovement> movementsBySourceItem = null!;
        private ILookup<Guid, SupplierControlAttribution> attributionsByMovement = null!;
        private ILookup<(Guid Journal, int Ordinal), SupplierControlAttribution> attributionsByLine = null!;
        private ILookup<(Guid Journal, int Ordinal), JournalLineRow> linesByOrdinal = null!;
        private ILookup<Guid, ControlEvidence> controlsByItem = null!;
        internal ILookup<Guid, JournalEntry> JournalsBySource { get; private set; } = null!;
        internal ILookup<Guid, SupplierApplication> ApplicationsById { get; private set; } = null!;
        internal ILookup<Guid, SupplierApplication> ApplicationsByFunding { get; private set; } = null!;
        private ILookup<Guid, SupplierApplication> applicationsByGroup = null!;
        internal ILookup<Guid, SupplierApplicationReversal> ReversalsById { get; private set; } = null!;
        private ILookup<Guid, SupplierApplicationReversal> reversalsByGroup = null!;
        internal ILookup<Guid, SupplierPaymentCorrection> PaymentCorrectionsByGroup { get; private set; } = null!;
        internal ILookup<Guid, JournalCorrectionGroup> CorrectionsByReversalSource { get; private set; } = null!;
        private ILookup<Guid, RecognitionSideEvent> recognitionById = null!;
        private ILookup<Guid, RecognitionUnit> recognitionUnitsById = null!;
        private ILookup<Guid, RecognitionOwner> recognitionOwnersByGroup = null!;
        private ILookup<Guid, RecognitionEventCorrection> recognitionCorrectionsByGroup = null!;
        private readonly Dictionary<Guid, bool> validSources = [];
        private readonly Dictionary<Guid, bool> validMovements = [];

        internal IEnumerable<T> Observe<T>(IEnumerable<T> rows)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (var row in rows)
            {
                cancellation.ThrowIfCancellationRequested();
                yield return row;
            }
        }

        internal IEnumerable<T> Evidence<TKey, T>(ILookup<TKey, T> index, TKey key) => Observe(index[key]);
        internal IEnumerable<T> Evidence<T>(ILookup<Guid, T> index, Guid? key) =>
            key.HasValue ? Evidence(index, key.Value) : Observe(Enumerable.Empty<T>());
        private ILookup<TKey, T> Index<TKey, T>(IEnumerable<T> rows, Func<T, TKey> key) => Observe(rows).ToLookup(key);

        internal void IndexEvidence()
        {
            MovementsByItem = Index(Movements, m => m.ItemId);
            MovementsByGroupSource = Index(Movements, m => (m.GroupId, m.SourceEventId));
            movementsById = Index(Movements, m => m.Id);
            movementsBySourceItem = Index(Movements, m => (m.SourceEventId, m.ItemId));
            attributionsByMovement = Index(Attributions, a => a.MovementId);
            attributionsByLine = Index(Attributions, a => (a.JournalId, a.Ordinal));
            linesByOrdinal = Index(Lines, l => (l.JournalId, l.Ordinal));
            controlsByItem = Index(Controls, c => c.ItemId);
            JournalsBySource = Index(Journals.Values, j => j.SourceEventId);
            ApplicationsById = Index(Applications, a => a.Id);
            ApplicationsByFunding = Index(Applications, a => a.FundingItemId);
            applicationsByGroup = Index(Applications, a => a.GroupId);
            ReversalsById = Index(Reversals, r => r.Id);
            reversalsByGroup = Index(Reversals, r => r.GroupId);
            PaymentCorrectionsByGroup = Index(PaymentCorrections, p => p.GroupId);
            CorrectionsByReversalSource = Index(Corrections, c => c.ReversalSourceEventId);
            recognitionById = Index(Recognition, r => r.Id);
            recognitionUnitsById = Index(RecognitionUnits, u => u.Id);
            recognitionOwnersByGroup = Index(RecognitionOwners, o => o.GroupId);
            recognitionCorrectionsByGroup = Index(RecognitionCorrections, c => c.CorrectionGroupId);
        }

        internal static async Task<Snapshot> Load(WorkbenchDbContext db, SupplierReportFilters f, CancellationToken ct)
        {
            var result = new Snapshot(f, ct);
            var groups = db.SupplierFinancialGroups.AsNoTracking().Where(g => g.Sequence <= f.GroupCeiling && g.RecordedAtUtc <= f.RecordedThrough);
            result.Groups = await groups.ToDictionaryAsync(g => g.Id, ct);
            result.Items = await db.SupplierOpenItems.AsNoTracking().Where(i => i.RecordedAtUtc <= f.RecordedThrough &&
                db.SupplierItemMovements.Any(m => m.ItemId == i.Id && groups.Any(g => g.Id == m.GroupId)))
                .Select(i => new SupplierOpenItem
                {
                    TenantId = i.TenantId,
                    Id = i.Id,
                    Kind = i.Kind,
                    SupplierId = i.SupplierId,
                    PurchaseOrderId = i.PurchaseOrderId,
                    BillId = i.BillId,
                    Currency = i.Currency,
                    SourceKind = i.SourceKind,
                    SourceId = i.SourceId,
                    SourceRevisionId = i.SourceRevisionId,
                    SourcePostingDate = i.SourcePostingDate,
                    DueDate = i.DueDate,
                    RecordedAtUtc = i.RecordedAtUtc
                }).ToListAsync(ct);
            result.Movements = await db.SupplierItemMovements.AsNoTracking().Where(m => groups.Any(g => g.Id == m.GroupId)).ToListAsync(ct);
            result.Attributions = await db.SupplierControlAttributions.AsNoTracking().Where(a => groups.Any(g => g.Id == a.GroupId)).ToListAsync(ct);
            var journals = db.JournalEntries.AsNoTracking().Where(j => j.Sequence <= f.JournalCeiling && j.RecordedAtUtc <= f.RecordedThrough);
            result.Journals = await journals.ToDictionaryAsync(j => j.Id, ct);
            // Proof TVFs compare the large immutable envelopes inside SQL. Readback only needs identities and dates.
            result.Sources = await db.JournalSourceEvents.AsNoTracking().Where(s => journals.Any(j => j.SourceEventId == s.Id))
                .Select(s => new JournalSourceEvent
                {
                    Id = s.Id,
                    TenantId = s.TenantId,
                    SourceKind = s.SourceKind,
                    SourceId = s.SourceId,
                    SourceRevision = s.SourceRevision,
                    EventKind = s.EventKind,
                    PostingDate = s.PostingDate,
                    RecordedAtUtc = s.RecordedAtUtc
                }).ToDictionaryAsync(s => s.Id, ct);
            result.Lines = await db.JournalLines.AsNoTracking().Where(l => journals.Any(j => j.Id == l.JournalId)).ToListAsync(ct);
            result.Applications = await db.SupplierApplications.AsNoTracking().Where(a => groups.Any(g => g.Id == a.GroupId)).ToListAsync(ct);
            result.Reversals = await db.SupplierApplicationReversals.AsNoTracking().Where(a => groups.Any(g => g.Id == a.GroupId)).ToListAsync(ct);
            result.PaymentCorrections = await db.SupplierPaymentCorrections.AsNoTracking().Where(a => groups.Any(g => g.Id == a.GroupId)).ToListAsync(ct);
            result.Corrections = await db.JournalCorrectionGroups.AsNoTracking().Where(c => journals.Any(j => j.Id == c.ReversalJournalId)).ToListAsync(ct);
            result.Recognition = await db.RecognitionSideEvents.AsNoTracking().Where(e => journals.Any(j => j.Id == e.JournalId))
                .Select(e => new RecognitionSideEvent
                {
                    Id = e.Id,
                    UnitId = e.UnitId,
                    Side = e.Side,
                    SourceAmount = e.SourceAmount,
                    JournalId = e.JournalId,
                    SourceId = e.SourceId,
                    SourceRevision = e.SourceRevision,
                    PostingDate = e.PostingDate,
                    RecordedAtUtc = e.RecordedAtUtc
                }).ToListAsync(ct);
            result.RecognitionUnits = await db.RecognitionUnits.AsNoTracking().Where(u => db.RecognitionSideEvents.Any(e => e.UnitId == u.Id && journals.Any(j => j.Id == e.JournalId))).ToListAsync(ct);
            result.RecognitionOwners = await db.Database.SqlQuery<RecognitionOwner>($"""
                SELECT g.Id GroupId,TRY_CONVERT(uniqueidentifier,JSON_VALUE(r.ResultJson,'$.correctionGroupId')) CorrectionId,
                    TRY_CONVERT(uniqueidentifier,ids.value) EventId
                FROM Purchasing.SupplierFinancialGroups g
                JOIN Purchasing.RecognitionGroupReceipts r ON r.TenantId=g.TenantId AND r.RequestId=g.Id AND r.RecordedAtUtc=g.RecordedAtUtc
                OUTER APPLY OPENJSON(r.ResultJson,'$.eventIds') ids
                WHERE g.TenantId={f.Tenant} AND g.Sequence<={f.GroupCeiling} AND g.RecordedAtUtc<={f.RecordedThrough}
                """).ToListAsync(ct);
            result.RecognitionCorrections = await db.RecognitionEventCorrections.AsNoTracking()
                .Where(c => groups.Any(g => g.SourceId == c.CorrectionGroupId)).ToListAsync(ct);
            result.Scale = await db.JournalPolicyFreezes.Select(p => (int?)p.Scale).SingleOrDefaultAsync(ct) ?? 2;
            result.Controls = await db.Database.SqlQuery<ControlEvidence>($"""
                SELECT i.Id ItemId,c.AccountId,c.AccountVersion,c.AccountPurpose
                FROM Purchasing.SupplierOpenItems i CROSS APPLY Purchasing.SupplierItemControl({f.Tenant},i.Id) c
                WHERE i.TenantId={f.Tenant} AND EXISTS(SELECT 1 FROM Purchasing.SupplierItemMovements m
                  JOIN Purchasing.SupplierFinancialGroups g ON g.TenantId=m.TenantId AND g.Id=m.GroupId
                  JOIN Accounting.JournalEntries j ON j.TenantId=m.TenantId AND j.SourceEventId=m.SourceEventId
                  WHERE m.TenantId=i.TenantId AND m.ItemId=i.Id AND m.EventKind='Open'
                    AND g.Sequence<={f.GroupCeiling} AND j.Sequence<={f.JournalCeiling}
                    AND g.RecordedAtUtc<={f.RecordedThrough} AND j.RecordedAtUtc<={f.RecordedThrough})
                """).ToListAsync(ct);
            return result;
        }

        internal bool Visible(SupplierItemMovement m) => m.PostingDate <= Filter.PostingThrough;
        internal bool Visible(JournalEntry j) => j.PostingDate <= Filter.PostingThrough;
    }
}
