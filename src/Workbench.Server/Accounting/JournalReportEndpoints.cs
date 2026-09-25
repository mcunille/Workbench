// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Workbench.Server.Persistence;

namespace Workbench.Server.Accounting;

public static class JournalReportEndpoints
{
    private const int DefaultPageSize = 50;
    private const int MaximumPageSize = 100;
    private static readonly DateOnly LatestPostingDate = new(9999, 12, 31);
    private static readonly JsonSerializerOptions CursorJson = new(JsonSerializerDefaults.Web);

    public static void MapJournalReports(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/beta/accounting")
            .WithTags("Accounting reports")
            .RequireAuthorization("AccountingReportsRead");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "private, no-store";
            return await next(context);
        });
        group.MapGet("/journals", BrowseJournals).Produces<JournalPage>().ProducesProblem(400).ProducesProblem(409).ProducesProblem(422);
        group.MapGet("/journals/{id:guid}", ReadJournal).Produces<JournalDetail>().ProducesProblem(404).ProducesProblem(409).ProducesProblem(422);
        group.MapGet("/accounts/{id:guid}/journal", BrowseAccount).Produces<AccountJournalPage>()
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(422);
        group.MapGet("/trial-balance", ReadTrialBalance).Produces<TrialBalancePage>().ProducesProblem(400).ProducesProblem(409).ProducesProblem(422);
    }

    private static Task<IResult> BrowseJournals(HttpContext http, string? postingThrough, string? recordedThrough, int? pageSize, string? cursor, WorkbenchDbContext database,
        IDataProtectionProvider protection, CancellationToken ct) =>
        JournalReportQueries.BrowseJournals(http, database, CursorProtector(protection), ct);

    private static Task<IResult> ReadJournal(Guid id, WorkbenchDbContext database, CancellationToken ct) =>
        JournalReportQueries.ReadJournal(id, database, ct);

    private static Task<IResult> BrowseAccount(Guid id, HttpContext http, string? postingThrough, string? recordedThrough, int? pageSize, string? cursor, WorkbenchDbContext database,
        IDataProtectionProvider protection, CancellationToken ct) =>
        JournalReportQueries.BrowseAccount(id, http, database, CursorProtector(protection), ct);

    private static Task<IResult> ReadTrialBalance(HttpContext http, string? postingThrough, string? recordedThrough, int? pageSize, string? cursor, WorkbenchDbContext database,
        IDataProtectionProvider protection, CancellationToken ct) =>
        JournalReportQueries.ReadTrialBalance(http, database, CursorProtector(protection), ct);

    private static IDataProtector CursorProtector(IDataProtectionProvider provider) =>
        provider.CreateProtector("Workbench.Accounting.JournalReportCursor.v1");

    internal sealed record CursorState(Guid TenantId, string Route, DateOnly PostingThrough,
        DateTimeOffset RecordedThrough, int PageSize, long? Sequence, Guid? Id,
        DateOnly? PostingDate, int? Ordinal);

    internal sealed record ReportFilter(DateOnly PostingThrough, DateTimeOffset? RecordedThrough,
        int PageSize, CursorState? Cursor);

    internal static bool TryReadFilter(HttpContext http, Guid tenantId, string route,
        IDataProtector protector, out ReportFilter? filter)
    {
        filter = null;
        var query = http.Request.Query;
        if (query.Keys.Any(key => key is not ("postingThrough" or "recordedThrough" or "pageSize" or "cursor")) ||
            query.Any(pair => pair.Value.Count != 1)) return false;
        var posting = LatestPostingDate;
        var rawPosting = query["postingThrough"].ToString();
        if (query.ContainsKey("postingThrough") &&
            (!DateOnly.TryParseExact(rawPosting, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out posting) || posting.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) != rawPosting))
            return false;
        DateTimeOffset? recorded = null;
        if (query.ContainsKey("recordedThrough"))
        {
            var rawRecorded = query["recordedThrough"].ToString();
            if (!Regex.IsMatch(rawRecorded, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|\+00:00)$", RegexOptions.CultureInvariant) ||
                !DateTimeOffset.TryParse(rawRecorded, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ||
                parsed.Offset != TimeSpan.Zero) return false;
            recorded = parsed;
        }
        var size = DefaultPageSize;
        if (query.ContainsKey("pageSize") &&
            (!int.TryParse(query["pageSize"], NumberStyles.None, CultureInfo.InvariantCulture, out size) ||
                size is < 1 or > MaximumPageSize)) return false;
        CursorState? cursor = null;
        if (query.ContainsKey("cursor"))
        {
            var encoded = query["cursor"].ToString();
            if (encoded.Length is < 1 or > 4096) return false;
            try
            {
                cursor = JsonSerializer.Deserialize<CursorState>(protector.Unprotect(Convert.FromBase64String(encoded)), CursorJson);
            }
            catch (Exception exception) when (exception is FormatException or CryptographicException or JsonException)
            {
                return false;
            }
            if (cursor is null || cursor.TenantId != tenantId || cursor.Route != route ||
                cursor.PageSize != size || (cursor.Id is null || cursor.Id == Guid.Empty) ||
                cursor.RecordedThrough.Offset != TimeSpan.Zero ||
                query.ContainsKey("postingThrough") && cursor.PostingThrough != posting ||
                recorded.HasValue && cursor.RecordedThrough != recorded.Value) return false;
            posting = cursor.PostingThrough;
            recorded = cursor.RecordedThrough;
            if (route == "journals" && (cursor.Sequence is null or < 1 || cursor.PostingDate is not null || cursor.Ordinal is not null) ||
                route == "trial-balance" && (cursor.Sequence is not null || cursor.PostingDate is not null || cursor.Ordinal is not null) ||
                route.StartsWith("account:", StringComparison.Ordinal) &&
                (cursor.Sequence is null or < 1 || cursor.PostingDate is null || cursor.Ordinal is null or < 1))
                return false;
        }
        filter = new(posting, recorded, size, cursor);
        return true;
    }

    internal static string MakeCursor(IDataProtector protector, CursorState cursor) =>
        Convert.ToBase64String(protector.Protect(JsonSerializer.SerializeToUtf8Bytes(cursor, CursorJson)));

    internal static IResult InvalidFilter() => Results.Problem(statusCode: 400, title: "Review the report filters and cursor.");
    internal static IResult Retry() => Results.Problem(statusCode: 409, title: "The report changed while reading. Retry the request.");
    internal static IResult Unavailable() => Results.Problem(statusCode: 404, title: "The requested accounting record is unavailable.");
}
