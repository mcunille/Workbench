// Copyright (c) 2026 The White Stag Collection.
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace Workbench.Server.Accounting;

internal sealed record SupplierReportFilters(Guid Tenant, string Route, Guid? SupplierId, Guid? PurchaseOrderId,
    Guid? BillId, DateOnly PostingThrough, DateTimeOffset? RecordedThrough, int PageSize,
    long? GroupCeiling = null, long? JournalCeiling = null, string? LastKey = null)
{
    private static IDataProtector Protector(IDataProtectionProvider provider) =>
        provider.CreateProtector("Workbench.Accounting.SupplierReportCursor.v1");

    internal static SupplierReportFilters? Parse(HttpContext http, Guid tenant, string route, IDataProtectionProvider protection)
    {
        var query = http.Request.Query;
        if (query.Keys.Any(k => k is not ("supplierId" or "purchaseOrderId" or "billId" or "postingThrough" or "recordedThrough" or "pageSize" or "cursor")) ||
            query.Any(p => p.Value.Count != 1)) return null;
        Guid? Id(string key) => query.ContainsKey(key) ? Guid.TryParseExact(query[key], "D", out var id) && id != Guid.Empty ? id : Guid.Empty : null;
        var supplier = Id("supplierId"); var po = Id("purchaseOrderId"); var bill = Id("billId");
        if (supplier == Guid.Empty || po == Guid.Empty || bill == Guid.Empty) return null;
        var posting = DateOnly.MaxValue;
        if (query.ContainsKey("postingThrough") && !DateOnly.TryParseExact(query["postingThrough"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out posting)) return null;
        DateTimeOffset? recorded = null;
        if (query.ContainsKey("recordedThrough"))
        {
            var value = query["recordedThrough"].ToString();
            if (!Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|\+00:00)$", RegexOptions.CultureInvariant) ||
                !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return null;
            recorded = parsed;
        }
        var size = 50;
        if (query.ContainsKey("pageSize") && (!int.TryParse(query["pageSize"], NumberStyles.None, CultureInfo.InvariantCulture, out size) || size is < 1 or > 200)) return null;
        var filter = new SupplierReportFilters(tenant, route, supplier, po, bill, posting, recorded, size);
        if (!query.ContainsKey("cursor")) return filter;
        var encoded = query["cursor"].ToString();
        if (encoded.Length is < 1 or > 4096) return null;
        try
        {
            var cursor = JsonSerializer.Deserialize<SupplierReportFilters>(Protector(protection).Unprotect(encoded));
            if (cursor is null || cursor.Tenant != tenant || cursor.Route != route || cursor.SupplierId != supplier ||
                cursor.PurchaseOrderId != po || cursor.BillId != bill || cursor.PageSize != size ||
                cursor.GroupCeiling is null or < 0 || cursor.JournalCeiling is null or < 0 ||
                cursor.RecordedThrough is null || cursor.RecordedThrough.Value.Offset != TimeSpan.Zero || string.IsNullOrEmpty(cursor.LastKey) ||
                query.ContainsKey("postingThrough") && cursor.PostingThrough != posting || recorded.HasValue && cursor.RecordedThrough != recorded) return null;
            return cursor;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException) { return null; }
    }

    internal string Cursor(IDataProtectionProvider protection, string lastKey) =>
        Protector(protection).Protect(JsonSerializer.Serialize(this with { LastKey = lastKey }));
    internal bool Matches(Purchasing.SupplierOpenItem item) =>
        (!SupplierId.HasValue || SupplierId == item.SupplierId) && (!PurchaseOrderId.HasValue || PurchaseOrderId == item.PurchaseOrderId) &&
        (!BillId.HasValue || BillId == item.BillId);
    internal bool Scoped => SupplierId.HasValue || PurchaseOrderId.HasValue || BillId.HasValue;
}
