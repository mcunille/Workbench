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

internal static class SupplierReconciliationQueries
{
    internal static Task<IResult> Reconcile(HttpContext http, WorkbenchDbContext db, IDataProtectionProvider protection, CancellationToken ct) =>
        Run(http, db, protection, "reconciliation", async snapshot =>
        {
            var rows = snapshot.Reconcile();
            var page = snapshot.Page(rows, r => $"{r.AccountId:D}:{r.Currency}:{r.ControlFamily}",
                r => (r.ControlFamily, Units(r.SubledgerAmount)), protection);
            return await Task.FromResult(Results.Ok(new SupplierReconciliationSummary(page,
                rows.All(r => r.IsComplete) && snapshot.UnknownCount == 0, snapshot.UnknownCount)));
        }, ct);

    internal static async Task<IResult> Run(HttpContext http, WorkbenchDbContext db, IDataProtectionProvider protection,
        string route, Func<Snapshot, Task<IResult>> read, CancellationToken ct)
    {
        var filter = SupplierReportFilters.Parse(http, db.TenantContext.RequireTenantId(), route, protection);
        if (filter is null) return JournalReportEndpoints.InvalidFilter();
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            // Resolve time and identity ceilings only after earlier tenant writers have committed.
            // Shared uses the same resource and principal as every Accounting writer.
            await using (var command = new SqlCommand("""
                DECLARE @result int,@resource nvarchar(255)=N'Accounting:'+CONVERT(nvarchar(36),@tenant);
                EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Shared',@LockOwner='Transaction',@LockTimeout=10000;
                IF @result<0 THROW 51010,'Supplier report coordination is busy.',1;
                SELECT SYSDATETIMEOFFSET() AT TIME ZONE 'UTC',
                    COALESCE((SELECT MAX(Sequence) FROM Purchasing.SupplierFinancialGroups WHERE TenantId=@tenant),0),
                    COALESCE((SELECT MAX(Sequence) FROM Accounting.JournalEntries WHERE TenantId=@tenant),0);
                """, (SqlConnection)db.Database.GetDbConnection(), (SqlTransaction)tx.GetDbTransaction()))
            {
                command.Parameters.AddWithValue("@tenant", filter.Tenant);
                await using var reader = await command.ExecuteReaderAsync(ct);
                await reader.ReadAsync(ct);
                filter = filter with
                {
                    RecordedThrough = filter.RecordedThrough ?? reader.GetFieldValue<DateTimeOffset>(0),
                    GroupCeiling = filter.GroupCeiling ?? reader.GetInt64(1),
                    JournalCeiling = filter.JournalCeiling ?? reader.GetInt64(2)
                };
            }
            if (filter.SupplierId.HasValue && !await db.Suppliers.AnyAsync(i => i.Id == filter.SupplierId, ct) ||
                filter.PurchaseOrderId.HasValue && !await db.DraftOrders.AnyAsync(i => i.Id == filter.PurchaseOrderId, ct) ||
                filter.BillId.HasValue && !await db.Database.SqlQuery<Guid>($"SELECT Id AS Value FROM Purchasing.SupplierReportBillIdentity({filter.Tenant},{filter.BillId.Value})").AnyAsync(ct)) return JournalReportEndpoints.Unavailable();
            var snapshot = await Snapshot.Load(db, filter, ct);
            var result = await read(snapshot);
            await tx.CommitAsync(ct);
            return result;
        }
        catch (SqlException e) when (e.Number is -2 or 1205 or 1222 or 51010) { return JournalReportEndpoints.Retry(); }
        catch (SqlException e) when (e.Number == 8115) { return Results.Problem(statusCode: 422, title: "Report totals exceed the supported range."); }
    }

    internal static BigInteger Units(decimal value) => new(value * 10000m);
    internal static BigInteger Units(string value)
    {
        var negative = value.StartsWith('-'); var parts = value.TrimStart('-').Split('.');
        var units = BigInteger.Parse(parts[0], CultureInfo.InvariantCulture) * 10000 +
            BigInteger.Parse(parts.Length == 1 ? "0" : parts[1].PadRight(4, '0'), CultureInfo.InvariantCulture);
        return negative ? -units : units;
    }
    internal static string Money(BigInteger units, int scale)
    {
        var absolute = BigInteger.Abs(units); var fraction = (absolute % 10000).ToString("D4", CultureInfo.InvariantCulture);
        if (scale is < 0 or > 4 || fraction.AsSpan(scale).IndexOfAnyExcept('0') >= 0)
            throw new InvalidOperationException("Supplier evidence exceeds its currency scale.");
        return (units.Sign < 0 ? "-" : "") + (absolute / 10000).ToString(CultureInfo.InvariantCulture) + (scale == 0 ? "" : "." + fraction[..scale]);
    }
    internal static string Family(string kind) => kind.StartsWith("Supplier", StringComparison.Ordinal) ? kind[8..] : kind;

    internal sealed class ControlEvidence
    {
        public Guid ItemId { get; set; }
        public Guid AccountId { get; set; }
        public Guid AccountVersion { get; set; }
        public string AccountPurpose { get; set; } = "";
    }

    internal sealed class RecognitionOwner
    {
        public Guid GroupId { get; set; }
        public Guid? CorrectionId { get; set; }
        public Guid? EventId { get; set; }
    }

    internal sealed class Snapshot(SupplierReportFilters filter)
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

        internal static async Task<Snapshot> Load(WorkbenchDbContext db, SupplierReportFilters f, CancellationToken ct)
        {
            var result = new Snapshot(f);
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
        internal bool ValidSource(SupplierOpenItem i)
        {
            var openings = Movements.Where(m => m.ItemId == i.Id && m.EventKind == "Open").ToArray();
            return Controls.Count(c => c.ItemId == i.Id) == 1 && openings.Length > 0 && openings.All(m =>
                Sources.TryGetValue(m.SourceEventId, out var source) && (source.SourceKind == "SupplierPayment" && source.EventKind == "Payment" || OpeningSourceMatches(m, i, source)));
        }
        internal BigInteger Balance(Guid id) => Movements.Where(m => m.ItemId == id && Visible(m)).Aggregate(BigInteger.Zero, (n, m) => n + Units(m.Amount));
        internal IEnumerable<SupplierOpenItem> SelectedItems => Items.Where(i => Filter.Matches(i) && Movements.Any(m => m.ItemId == i.Id && Visible(m)));
        internal SupplierReportTotals Totals(IEnumerable<(string Kind, BigInteger Amount)> amounts)
        {
            var sums = amounts.GroupBy(x => Family(x.Kind)).ToDictionary(g => g.Key, g => g.Aggregate(BigInteger.Zero, (n, a) => n + a.Amount));
            BigInteger Get(string kind) => sums.GetValueOrDefault(kind);
            return new(Money(Get("Payable"), Scale), Money(Get("Advance"), Scale), Money(Get("CreditReceivable"), Scale),
                Money(Get("RefundClearing"), Scale), Money(Get("Payable") + Get("RefundClearing") - Get("Advance") - Get("CreditReceivable"), Scale));
        }
        internal SupplierReportPage<T> Page<T>(IReadOnlyList<T> rows, Func<T, string> key, Func<T, (string, BigInteger)> amount, IDataProtectionProvider protection)
        {
            var ordered = rows.OrderBy(key, StringComparer.Ordinal).ToArray();
            var page = ordered.Where(r => Filter.LastKey is null || StringComparer.Ordinal.Compare(key(r), Filter.LastKey) > 0).Take(Filter.PageSize + 1).ToList();
            var more = page.Count > Filter.PageSize; if (more) page.RemoveAt(page.Count - 1);
            return new(page, more ? Filter.Cursor(protection, key(page[^1])) : null, Filter.PostingThrough, Filter.RecordedThrough!.Value,
                Totals(ordered.Select(amount)), Totals(page.Select(amount)));
        }

        private bool ExactAttribution(SupplierControlAttribution a, SupplierItemMovement m, SupplierOpenItem item)
        {
            if (a.GroupId != m.GroupId || !Journals.TryGetValue(a.JournalId, out var journal) || journal.SourceEventId != m.SourceEventId ||
                journal.PostingDate != m.PostingDate || journal.Currency != item.Currency || journal.RecordedAtUtc != Groups[m.GroupId].RecordedAtUtc ||
                m.RecordedAtUtc != Groups[m.GroupId].RecordedAtUtc) return false;
            return Lines.Any(l => l.JournalId == a.JournalId && l.Ordinal == a.Ordinal && l.AccountId == a.AccountId &&
                l.AccountVersion == a.AccountVersion && l.AccountPurpose == a.AccountPurpose && Family(l.AccountPurpose) == item.Kind) &&
                Controls.Any(c => c.ItemId == item.Id && c.AccountId == a.AccountId && c.AccountVersion == a.AccountVersion && c.AccountPurpose == a.AccountPurpose);
        }

        // Prove complete source/item effects, not one-to-one gross payment movements.
        // Inverse effects retain exact historical line ownership through Accounting correction links.
        private bool ValidMovement(SupplierItemMovement m, SupplierOpenItem item)
        {
            if (!ValidSource(item) || !Sources.TryGetValue(m.SourceEventId, out var source) ||
                source.PostingDate != m.PostingDate || source.RecordedAtUtc != Groups[m.GroupId].RecordedAtUtc || m.RecordedAtUtc != source.RecordedAtUtc) return false;
            var attrs = Attributions.Where(a => a.MovementId == m.Id).ToArray();
            if (attrs.Any(a => !ExactAttribution(a, m, item)) || attrs.Length > 1) return false;
            if (source.SourceKind == "SupplierPayment" && source.EventKind == "Payment")
            {
                // SupplierPaymentControl validates all original payment movements and exact debt ordinals.
                return Controls.Any(c => c.ItemId == source.SourceId) &&
                    (m.EventKind == "Open" && m.ItemId == source.SourceId || m.EventKind == "Apply" && Applications.Any(a =>
                        a.GroupId == m.GroupId && a.FundingItemId == source.SourceId && (a.FundingItemId == m.ItemId || a.DebtItemId == m.ItemId) && -a.Amount == m.Amount));
            }
            var correction = Corrections.SingleOrDefault(c => c.ReversalSourceEventId == m.SourceEventId);
            if (correction is not null)
            {
                if (!Sources.TryGetValue(correction.OriginalSourceEventId, out var originalSource)) return false;
                var ownsPaymentInverse = originalSource.SourceKind == "SupplierPayment" && originalSource.EventKind == "Payment" && PaymentCorrections.Any(p => p.GroupId == m.GroupId && p.OriginalPaymentId == originalSource.SourceId);
                var ownsCompensation = originalSource.SourceKind == "SupplierApplicationReversal" && originalSource.EventKind == "Reverse" && Reversals.Any(r => r.Id == originalSource.SourceId &&
                    Applications.Any(a => a.Id == r.ApplicationId && PaymentCorrections.Any(p => p.GroupId == m.GroupId && p.OriginalPaymentId == a.FundingItemId &&
                        Movements.Any(open => open.ItemId == p.OriginalPaymentId && open.EventKind == "Open" && open.GroupId == a.GroupId))));
                var ownerGroup = Groups[m.GroupId];
                var ownsRecognitionInverse = originalSource.SourceKind == "PurchaseRecognition" && originalSource.EventKind == "Invoice" && m.RecognitionEventId == originalSource.SourceId &&
                    ownerGroup.Operation == "CorrectSource" && RecognitionOwners.Any(o => o.GroupId == m.GroupId && o.CorrectionId == ownerGroup.SourceId) &&
                    RecognitionCorrections.Any(c => c.CorrectionGroupId == ownerGroup.SourceId && c.OriginalEventId == m.RecognitionEventId && c.AccountingCorrectionGroupId == correction.Id);
                if (!ownsPaymentInverse && !ownsCompensation && !ownsRecognitionInverse) return false;
                var originals = Movements.Where(o => o.SourceEventId == correction.OriginalSourceEventId && o.ItemId == m.ItemId && o.Amount == -m.Amount).ToArray();
                return originals.Any(o =>
                {
                    var originalAttrs = Attributions.Where(a => a.MovementId == o.Id).ToArray();
                    return attrs.Length == originalAttrs.Length && attrs.All(a => originalAttrs.Any(old => old.JournalId == correction.OriginalJournalId &&
                        a.JournalId == correction.ReversalJournalId && old.Ordinal == a.Ordinal && old.AccountId == a.AccountId && old.AccountVersion == a.AccountVersion && old.Amount == -a.Amount));
                });
            }
            if (m.EventKind == "Apply")
                return attrs.Length == 1 && attrs[0].Amount == m.Amount && Applications.Any(a => a.GroupId == m.GroupId &&
                    source.SourceId == a.Id && -a.Amount == m.Amount && (a.FundingItemId == m.ItemId || a.DebtItemId == m.ItemId));
            if (m.EventKind == "ReverseApplication")
                return attrs.Length == 1 && attrs[0].Amount == m.Amount && Reversals.Any(r => r.GroupId == m.GroupId && r.Id == source.SourceId &&
                    Applications.Any(a => a.Id == r.ApplicationId && a.Amount == m.Amount && (a.FundingItemId == m.ItemId || a.DebtItemId == m.ItemId)));
            if (m.EventKind != "Open" || attrs.Length != 1 || attrs[0].Amount != m.Amount) return false;
            return OpeningSourceMatches(m, item, source);
        }

        private bool OpeningSourceMatches(SupplierItemMovement m, SupplierOpenItem item, JournalSourceEvent source)
        {
            if (source.SourceKind != "PurchaseRecognition") return item.SourceId == source.SourceId && item.SourceRevisionId == source.SourceRevision;
            var recognition = Recognition.SingleOrDefault(r => r.Id == m.RecognitionEventId);
            var group = Groups[m.GroupId];
            var ownsOpening = RecognitionOwners.Any(o => o.GroupId == group.Id && o.EventId == m.RecognitionEventId &&
                (group.Operation == "OpenRecognitionPayable" && o.CorrectionId is null && (group.SourceId == item.BillId ||
                    RecognitionOwners.Any(member => member.GroupId == group.Id && member.EventId == group.SourceId)) ||
                 group.Operation == "CorrectSource" && o.CorrectionId == group.SourceId && RecognitionCorrections.Any(c =>
                    c.CorrectionGroupId == group.SourceId && c.ReplacementEventId == m.RecognitionEventId)));
            if (!ownsOpening) return false;
            return recognition is not null && recognition.Side == "Invoice" && recognition.SourceAmount == m.Amount &&
                Journals.TryGetValue(recognition.JournalId ?? Guid.Empty, out var openingJournal) && openingJournal.SourceEventId == m.SourceEventId && source.SourceId == recognition.Id && source.SourceRevision == recognition.SourceRevision &&
                item.SourceRevisionId == recognition.SourceRevision && item.SourceId == (item.BillId ?? recognition.Id) &&
                (!item.BillId.HasValue || recognition.SourceId == item.BillId) && RecognitionUnits.Any(u => u.Id == recognition.UnitId &&
                    u.SupplierId == item.SupplierId && u.PurchaseOrderId == item.PurchaseOrderId && u.Currency == item.Currency);
        }

        internal List<SupplierControlBalance> Reconcile()
        {
            var items = Items.ToDictionary(i => i.Id);
            var moves = Movements.Where(Visible).ToArray();
            var knownLines = new HashSet<(Guid, int)>();
            var buckets = new Dictionary<(Guid? Account, string Currency, string Family), Bucket>();
            Bucket Get(Guid? account, string currency, string family)
            {
                var key = (account, currency, Family(family));
                if (!buckets.TryGetValue(key, out var b)) buckets[key] = b = new();
                return b;
            }
            foreach (var item in SelectedItems)
            {
                var control = Controls.FirstOrDefault(c => c.ItemId == item.Id);
                var bucket = Get(control?.AccountId, item.Currency, item.Kind);
                bucket.Subledger += Balance(item.Id);
                foreach (var m in moves.Where(m => m.ItemId == item.Id))
                {
                    if (!ValidMovement(m, item)) bucket.Invalid++;
                    var attrs = Attributions.Where(a => a.MovementId == m.Id).ToArray();
                    if (attrs.Length > 1) bucket.Duplicate += attrs.Length - 1;
                }
            }
            foreach (var line in Lines.Where(l => Family(l.AccountPurpose) is "Payable" or "Advance" or "CreditReceivable" or "RefundClearing"))
            {
                var journal = Journals[line.JournalId]; if (!Visible(journal)) continue;
                var attrs = Attributions.Where(a => a.JournalId == line.JournalId && a.Ordinal == line.Ordinal).ToArray();
                var owned = attrs.Select(a => (Attr: a, Move: moves.SingleOrDefault(m => m.Id == a.MovementId)))
                    .Where(x => x.Move is not null && items.ContainsKey(x.Move.ItemId)).ToArray();
                var valid = owned.Where(x => ExactAttribution(x.Attr, x.Move!, items[x.Move!.ItemId]) && ValidMovement(x.Move!, items[x.Move!.ItemId])).ToArray();
                var amount = Units(Family(line.AccountPurpose) is "Payable" or "RefundClearing" ? line.Credit - line.Debit : line.Debit - line.Credit);
                var covered = valid.Aggregate(BigInteger.Zero, (n, x) => n + Units(x.Attr.Amount));
                var unknown = valid.Length != attrs.Length || attrs.Length == 0 || covered != amount;
                if (unknown) UnknownCount++;
                var selected = valid.Where(x => Filter.Matches(items[x.Move!.ItemId])).ToArray();
                if (!Filter.Scoped || selected.Length > 0 || unknown)
                {
                    var b = Get(line.AccountId, journal.Currency, line.AccountPurpose);
                    b.Journal += !Filter.Scoped || unknown ? amount : selected.Aggregate(BigInteger.Zero, (n, x) => n + Units(x.Attr.Amount));
                    if (attrs.Length == 0 || covered != amount) b.Missing++;
                    if (valid.Length != attrs.Length) b.Invalid += attrs.Length - valid.Length;
                }
                knownLines.Add((line.JournalId, line.Ordinal));
            }
            foreach (var a in Attributions.Where(a => !knownLines.Contains((a.JournalId, a.Ordinal))))
            {
                var m = moves.SingleOrDefault(m => m.Id == a.MovementId);
                if (m is not null && items.TryGetValue(m.ItemId, out var item) && Filter.Matches(item)) Get(a.AccountId, item.Currency, a.AccountPurpose).Missing++;
            }
            // Account totals alone cannot conceal offsetting source/item corruption.
            foreach (var effects in moves.GroupBy(m => (m.SourceEventId, m.ItemId)))
            {
                if (!items.TryGetValue(effects.Key.ItemId, out var item) || !Filter.Matches(item)) continue;
                var expected = effects.Aggregate(BigInteger.Zero, (n, m) => n + Units(m.Amount));
                var actual = effects.SelectMany(m => Attributions.Where(a => a.MovementId == m.Id)).Aggregate(BigInteger.Zero, (n, a) => n + Units(a.Amount));
                if (expected != actual) Get(Controls.FirstOrDefault(c => c.ItemId == item.Id)?.AccountId, item.Currency, item.Kind).Missing++;
            }
            return buckets.Select(p => new SupplierControlBalance(p.Key.Account, p.Key.Currency, p.Key.Family,
                Money(p.Value.Journal, Scale), Money(p.Value.Subledger, Scale), Money(p.Value.Journal - p.Value.Subledger, Scale),
                p.Value.Missing, p.Value.Duplicate, p.Value.Invalid, p.Value.Journal == p.Value.Subledger && p.Value.Missing + p.Value.Duplicate + p.Value.Invalid == 0)).ToList();
        }
        private sealed class Bucket
        {
            internal BigInteger Journal, Subledger;
            internal int Missing, Duplicate, Invalid;
        }
    }
}
