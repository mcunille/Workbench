// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Persistence;

namespace Workbench.Server.Accounting;

public sealed record AccountingPeriodItem(DateOnly PeriodStart, DateOnly PeriodEnd,
    DateOnly FiscalYearStart, string State, Guid? ClosureId, DateTimeOffset? ClosedAtUtc);
public sealed record AccountingPeriodPage(IReadOnlyList<AccountingPeriodItem> Items);

internal static class AccountingPeriodReports
{
    internal static async Task<IResult> Read(HttpContext http,
        [FromQuery(Name = "from")] string periodFrom,
        [FromQuery(Name = "through")] string periodThrough,
        WorkbenchDbContext database, CancellationToken ct)
    {
        var query = http.Request.Query;
        if (query.Count != 2 || !query.ContainsKey("from") || !query.ContainsKey("through") ||
            query["from"].Count != 1 || query["through"].Count != 1 ||
            !TryMonth(query["from"].ToString(), out var from) ||
            !TryMonth(query["through"].ToString(), out var through))
            return InvalidRange();
        var first = MonthIndex(from);
        var last = MonthIndex(through);
        if (last < first || last - first >= 120) return InvalidRange();

        try
        {
            await using var tx = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var configuration = await database.AccountingConfigurations.AsNoTracking().SingleOrDefaultAsync(ct);
            AccountingConfiguration? setup = null;
            if (configuration is not null)
                setup = JsonSerializer.Deserialize<AccountingConfiguration>(configuration.Payload, AccountingEndpoints.JsonOptions);
            var start = setup?.Policies.PlannedStartDate;
            var fiscalMonth = setup?.Policies.FiscalStartMonth;
            if (start is null || fiscalMonth is null or < 1 or > 12 ||
                setup?.Policies.Currency is null || setup.Policies.Scale is null || setup.Policies.StartApproach is null)
                return Results.Problem(statusCode: 409, title: "Accounting calendar configuration is incomplete.");

            var closures = await database.AccountingPeriodClosures.AsNoTracking()
                .Where(c => c.PeriodStart >= from && c.PeriodStart <= through)
                .ToDictionaryAsync(c => c.PeriodStart, ct);
            var items = new List<AccountingPeriodItem>();
            for (var index = first; index <= last; index++)
            {
                var year = (index - 1) / 12 + 1;
                var month = (index - 1) % 12 + 1;
                var monthStart = new DateOnly(year, month, 1);
                var end = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
                if (end < start.Value) continue;
                var fiscalYear = month < fiscalMonth.Value ? year - 1 : year;
                if (fiscalYear < 1)
                    return Results.Problem(statusCode: 409, title: "The fiscal calendar cannot represent the requested month.");
                closures.TryGetValue(monthStart, out var closure);
                items.Add(new(monthStart, end, new DateOnly(fiscalYear, fiscalMonth.Value, 1),
                    closure is null ? "Open" : "Closed", closure?.Id, closure?.RecordedAtUtc));
            }
            await tx.CommitAsync(ct);
            return Results.Ok(new AccountingPeriodPage(items));
        }
        catch (SqlException error) when (error.Number is -2 or 1205 or 1222)
        {
            return JournalReportEndpoints.Retry();
        }
    }

    private static bool TryMonth(string raw, out DateOnly month) =>
        DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out month) &&
        month.Day == 1 && month.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == raw;

    private static int MonthIndex(DateOnly date) => (date.Year - 1) * 12 + date.Month;
    private static IResult InvalidRange() => Results.Problem(statusCode: 400, title: "Use an inclusive range of 1 to 120 calendar month starts.");
}
