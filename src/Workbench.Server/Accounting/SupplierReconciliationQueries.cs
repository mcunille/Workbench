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
    internal static Task<IResult> Reconcile(HttpContext http, WorkbenchDbContext db, IDataProtectionProvider protection, CancellationToken ct) =>
        Run(http, db, protection, "reconciliation", async snapshot =>
        {
            var rows = snapshot.Reconcile();
            var page = snapshot.Page(rows, r => $"{r.AccountId:D}:{r.Currency}:{r.ControlFamily}",
                r => (r.ControlFamily, Units(r.SubledgerAmount)), protection);
            return await Task.FromResult(Results.Ok(new SupplierReconciliationSummary(page,
                snapshot.Observe(rows).All(r => r.IsComplete) && snapshot.UnknownCount == 0, snapshot.UnknownCount)));
        }, ct);

    internal static async Task<IResult> Run(HttpContext http, WorkbenchDbContext db, IDataProtectionProvider protection,
        string route, Func<Snapshot, Task<IResult>> read, CancellationToken ct)
    {
        var filter = SupplierReportFilters.Parse(http, db.TenantContext.RequireTenantId(), route, protection);
        if (filter is null) return JournalReportEndpoints.InvalidFilter();
        try
        {
            Snapshot snapshot;
            await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
            {
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
                snapshot = await Snapshot.Load(db, filter, ct);
                await tx.CommitAsync(ct);
            }
            // Only capture needs writer coordination. Indexing and response processing use immutable evidence.
            ct.ThrowIfCancellationRequested();
            snapshot.IndexEvidence();
            return await read(snapshot);
        }
        catch (SqlException e) when (e.Number == 50903) { return Results.Problem(statusCode: 403, title: "Current evidence access is required."); }
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

    internal sealed partial class Snapshot
    {
        internal BigInteger Balance(Guid id) => Evidence(MovementsByItem, id).Where(Visible).Aggregate(BigInteger.Zero, (n, m) => n + Units(m.Amount));
        internal IEnumerable<SupplierOpenItem> SelectedItems => Observe(Items).Where(i => Filter.Matches(i) && Evidence(MovementsByItem, i.Id).Any(Visible));
        internal SupplierReportTotals Totals(IEnumerable<(string Kind, BigInteger Amount)> amounts)
        {
            var sums = Observe(amounts).GroupBy(x => Family(x.Kind)).ToDictionary(g => g.Key, g => Observe(g).Aggregate(BigInteger.Zero, (n, a) => n + a.Amount));
            BigInteger Get(string kind) => sums.GetValueOrDefault(kind);
            return new(Money(Get("Payable"), Scale), Money(Get("Advance"), Scale), Money(Get("CreditReceivable"), Scale),
                Money(Get("RefundClearing"), Scale), Money(Get("Payable") + Get("RefundClearing") - Get("Advance") - Get("CreditReceivable"), Scale));
        }
        internal SupplierReportPage<T> Page<T>(IReadOnlyList<T> rows, Func<T, string> key, Func<T, (string, BigInteger)> amount, IDataProtectionProvider protection)
        {
            T[] ordered;
            try
            {
                ordered = Observe(rows).OrderBy(key, Comparer<string>.Create((a, b) =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    return StringComparer.Ordinal.Compare(a, b);
                })).ToArray();
            }
            catch (InvalidOperationException e) when (e.InnerException is OperationCanceledException)
            {
                // Array sorting wraps comparer exceptions; retain request cancellation semantics.
                cancellation.ThrowIfCancellationRequested();
                throw;
            }
            var page = Observe(ordered).Where(r => Filter.LastKey is null || StringComparer.Ordinal.Compare(key(r), Filter.LastKey) > 0).Take(Filter.PageSize + 1).ToList();
            var more = page.Count > Filter.PageSize; if (more) page.RemoveAt(page.Count - 1);
            return new(page, more ? Filter.Cursor(protection, key(page[^1])) : null, Filter.PostingThrough, Filter.RecordedThrough!.Value,
                Totals(ordered.Select(amount)), Totals(page.Select(amount)));
        }

        internal List<SupplierControlBalance> Reconcile()
        {
            cancellation.ThrowIfCancellationRequested();
            UnknownCount = 0;
            var items = Observe(Items).ToDictionary(i => i.Id);
            var moves = Observe(Movements).Where(Visible).ToArray();
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
                var control = Evidence(controlsByItem, item.Id).FirstOrDefault();
                var bucket = Get(control?.AccountId, item.Currency, item.Kind);
                bucket.Subledger += Balance(item.Id);
                foreach (var m in Evidence(MovementsByItem, item.Id).Where(Visible))
                {
                    if (!ValidMovement(m, item)) bucket.Invalid++;
                    var attrs = Evidence(attributionsByMovement, m.Id).ToArray();
                    if (attrs.Length > 1) bucket.Duplicate += attrs.Length - 1;
                }
            }
            foreach (var line in Observe(Lines).Where(l => Family(l.AccountPurpose) is "Payable" or "Advance" or "CreditReceivable" or "RefundClearing"))
            {
                var journal = Journals[line.JournalId]; if (!Visible(journal)) continue;
                var attrs = Evidence(attributionsByLine, (line.JournalId, line.Ordinal)).ToArray();
                var owned = Observe(attrs).Select(a => (Attr: a, Move: Evidence(movementsById, a.MovementId).Where(Visible).SingleOrDefault()))
                    .Where(x => x.Move is not null && items.ContainsKey(x.Move.ItemId)).ToArray();
                var valid = Observe(owned).Where(x => ExactAttribution(x.Attr, x.Move!, items[x.Move!.ItemId]) && ValidMovement(x.Move!, items[x.Move!.ItemId])).ToArray();
                var amount = Units(Family(line.AccountPurpose) is "Payable" or "RefundClearing" ? line.Credit - line.Debit : line.Debit - line.Credit);
                var covered = Observe(valid).Aggregate(BigInteger.Zero, (n, x) => n + Units(x.Attr.Amount));
                var unknown = valid.Length != attrs.Length || attrs.Length == 0 || covered != amount;
                if (unknown) UnknownCount++;
                var selected = Observe(valid).Where(x => Filter.Matches(items[x.Move!.ItemId])).ToArray();
                if (!Filter.Scoped || selected.Length > 0 || unknown)
                {
                    var b = Get(line.AccountId, journal.Currency, line.AccountPurpose);
                    b.Journal += !Filter.Scoped || unknown ? amount : Observe(selected).Aggregate(BigInteger.Zero, (n, x) => n + Units(x.Attr.Amount));
                    if (attrs.Length == 0 || covered != amount) b.Missing++;
                    if (valid.Length != attrs.Length) b.Invalid += attrs.Length - valid.Length;
                }
                knownLines.Add((line.JournalId, line.Ordinal));
            }
            foreach (var a in Observe(Attributions).Where(a => !knownLines.Contains((a.JournalId, a.Ordinal))))
            {
                var m = Evidence(movementsById, a.MovementId).Where(Visible).SingleOrDefault();
                if (m is not null && items.TryGetValue(m.ItemId, out var item) && Filter.Matches(item)) Get(a.AccountId, item.Currency, a.AccountPurpose).Missing++;
            }
            // Account totals alone cannot conceal offsetting source/item corruption.
            foreach (var effects in Observe(Observe(moves).GroupBy(m => (m.SourceEventId, m.ItemId))))
            {
                if (!items.TryGetValue(effects.Key.ItemId, out var item) || !Filter.Matches(item)) continue;
                var expected = Observe(effects).Aggregate(BigInteger.Zero, (n, m) => n + Units(m.Amount));
                var actual = Observe(effects).SelectMany(m => Evidence(attributionsByMovement, m.Id)).Aggregate(BigInteger.Zero, (n, a) => n + Units(a.Amount));
                if (expected != actual) Get(Evidence(controlsByItem, item.Id).FirstOrDefault()?.AccountId, item.Currency, item.Kind).Missing++;
            }
            return Observe(buckets).Select(p => new SupplierControlBalance(p.Key.Account, p.Key.Currency, p.Key.Family,
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
