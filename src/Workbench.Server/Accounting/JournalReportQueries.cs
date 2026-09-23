// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Workbench.Server.Persistence;
using static Workbench.Server.Accounting.JournalReportEndpoints;

namespace Workbench.Server.Accounting;

internal static class JournalReportQueries
{
    private const string HeaderSelect = """
        SELECT j.Id,j.Sequence,j.SourceEventId,s.SourceKind,s.SourceId,s.SourceRevision,s.EventKind,
            j.ConfigurationVersion,j.Currency,j.Scale,j.DocumentDate,j.EffectiveDate,j.PostingDate,
            j.RecordedAtUtc,j.ActorId,j.Reference,j.Reason,j.DebitTotal,j.CreditTotal
        FROM Accounting.JournalEntries j
        JOIN Accounting.SourceEvents s ON s.TenantId=j.TenantId AND s.Id=j.SourceEventId
        """;

    internal static async Task<IResult> BrowseJournals(HttpContext http, WorkbenchDbContext database,
        IDataProtector protector, CancellationToken ct)
    {
        var tenant = database.TenantContext.RequireTenantId();
        if (!TryReadFilter(http, tenant, "journals", protector, out var filter)) return InvalidFilter();
        try
        {
            await using var tx = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var connection = (SqlConnection)database.Database.GetDbConnection();
            var sqlTx = (SqlTransaction)tx.GetDbTransaction();
            var recorded = filter!.RecordedThrough ?? await DatabaseNow(connection, sqlTx, ct);
            var rows = new List<JournalHeader>();
            await using (var command = Command(connection, sqlTx, HeaderSelect + "\n" + """
                WHERE j.TenantId=@tenant AND j.PostingDate<=@posting AND j.RecordedAtUtc<=@recorded
                  AND (@sequence IS NULL OR j.Sequence>@sequence OR (j.Sequence=@sequence AND j.Id>@id))
                ORDER BY j.Sequence,j.Id OFFSET 0 ROWS FETCH NEXT @limit ROWS ONLY
                """, tenant, filter.PostingThrough, recorded))
            {
                command.Parameters.Add(new SqlParameter("@sequence", SqlDbType.BigInt) { Value = (object?)filter.Cursor?.Sequence ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = (object?)filter.Cursor?.Id ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@limit", SqlDbType.Int) { Value = filter.PageSize + 1 });
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) rows.Add(ReadHeader(reader));
            }
            var hasMore = rows.Count > filter.PageSize;
            if (hasMore) rows.RemoveAt(rows.Count - 1);
            var totals = await Totals(connection, sqlTx, "Accounting.JournalEntries j",
                "j.TenantId=@tenant AND j.PostingDate<=@posting AND j.RecordedAtUtc<=@recorded",
                "j.DebitTotal", "j.CreditTotal", tenant, filter.PostingThrough, recorded, ct);
            var pageTotals = SumSmall(rows.Select(row => (row.DebitTotal, row.CreditTotal)));
            var currency = await Currency(connection, sqlTx, tenant, ct);
            var last = rows.LastOrDefault();
            var next = hasMore && last is not null ? MakeCursor(protector,
                new CursorState(tenant, "journals", filter.PostingThrough, recorded, filter.PageSize,
                    last.Sequence, last.Id, null, null)) : null;
            await tx.CommitAsync(ct);
            return Results.Ok(new JournalPage(rows, next, filter.PostingThrough, recorded,
                FormatTotals(totals, currency.Scale), FormatTotals(pageTotals, currency.Scale)));
        }
        catch (SqlException e) when (e.Number == 8115) { return Results.Problem(statusCode: 422, title: "Report totals exceed the supported range."); }
        catch (SqlException e) when (Retryable(e)) { return Retry(); }
    }

    internal static async Task<IResult> ReadJournal(Guid id, WorkbenchDbContext database, CancellationToken ct)
    {
        var tenant = database.TenantContext.RequireTenantId();
        try
        {
            await using var tx = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var connection = (SqlConnection)database.Database.GetDbConnection();
            var sqlTx = (SqlTransaction)tx.GetDbTransaction();
            JournalHeader? header = null;
            await using (var command = new SqlCommand(HeaderSelect + " WHERE j.TenantId=@tenant AND j.Id=@id", connection, sqlTx))
            {
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = tenant });
                command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct)) header = ReadHeader(reader);
            }
            if (header is null) return Unavailable();
            JournalSourceEvidence source;
            await using (var command = new SqlCommand("""
                SELECT Id,SourceKind,SourceId,SourceRevision,EventKind,RuleVersion,ActorId,
                    DocumentDate,EffectiveDate,PostingDate,Reference,Reason,SnapshotJson,
                    CONVERT(varchar(64),SnapshotSha256,2),RecordedAtUtc
                FROM Accounting.SourceEvents WHERE TenantId=@tenant AND Id=@source
                """, connection, sqlTx))
            {
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = tenant });
                command.Parameters.Add(new SqlParameter("@source", SqlDbType.UniqueIdentifier) { Value = header.SourceEventId });
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Journal source evidence is missing.");
                source = new(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetGuid(3),
                    reader.GetString(4), reader.GetInt32(5), reader.GetGuid(6), Date(7, reader), Date(8, reader),
                    Date(9, reader), NullableString(10, reader), NullableString(11, reader), reader.GetString(12),
                    reader.GetString(13), reader.GetFieldValue<DateTimeOffset>(14));
            }
            var lines = new List<JournalLine>();
            await using (var command = new SqlCommand("""
                SELECT Ordinal,AccountId,AccountVersion,AccountCode,AccountName,AccountType,AccountPurpose,
                    Debit,Credit FROM Accounting.JournalLines
                WHERE TenantId=@tenant AND JournalId=@id ORDER BY Ordinal
                """, connection, sqlTx))
            {
                command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = tenant });
                command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    lines.Add(new(reader.GetInt32(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3),
                        reader.GetString(4), reader.GetString(5), reader.GetString(6),
                        FormatSmall(reader.GetDecimal(7), header.Scale), FormatSmall(reader.GetDecimal(8), header.Scale)));
            }
            await tx.CommitAsync(ct);
            return Results.Ok(new JournalDetail(header, lines, source));
        }
        catch (SqlException e) when (e.Number == 8115) { return Results.Problem(statusCode: 422, title: "Report totals exceed the supported range."); }
        catch (SqlException e) when (Retryable(e)) { return Retry(); }
    }

    internal static async Task<IResult> BrowseAccount(Guid id, HttpContext http, WorkbenchDbContext database,
        IDataProtector protector, CancellationToken ct)
    {
        var tenant = database.TenantContext.RequireTenantId();
        var route = $"account:{id:D}";
        if (!TryReadFilter(http, tenant, route, protector, out var filter)) return InvalidFilter();
        try
        {
            await using var tx = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var connection = (SqlConnection)database.Database.GetDbConnection();
            var sqlTx = (SqlTransaction)tx.GetDbTransaction();
            if (!await AccountExists(connection, sqlTx, tenant, id, ct)) return Unavailable();
            var recorded = filter!.RecordedThrough ?? await DatabaseNow(connection, sqlTx, ct);
            var rows = new List<AccountJournalLine>();
            await using (var command = Command(connection, sqlTx, """
                SELECT j.Id,j.Sequence,j.SourceEventId,l.Ordinal,j.PostingDate,j.RecordedAtUtc,j.Currency,j.Scale,
                    l.AccountCode,l.AccountName,l.AccountType,l.AccountPurpose,l.Debit,l.Credit
                FROM Accounting.JournalLines l
                JOIN Accounting.JournalEntries j ON j.TenantId=l.TenantId AND j.Id=l.JournalId
                WHERE l.TenantId=@tenant AND l.AccountId=@account AND j.PostingDate<=@posting
                    AND j.RecordedAtUtc<=@recorded
                    AND (@date IS NULL OR j.PostingDate>@date OR
                         (j.PostingDate=@date AND j.Sequence>@sequence) OR
                         (j.PostingDate=@date AND j.Sequence=@sequence AND l.Ordinal>@ordinal) OR
                         (j.PostingDate=@date AND j.Sequence=@sequence AND l.Ordinal=@ordinal AND j.Id>@id))
                ORDER BY j.PostingDate,j.Sequence,l.Ordinal,j.Id
                OFFSET 0 ROWS FETCH NEXT @limit ROWS ONLY
                """, tenant, filter.PostingThrough, recorded))
            {
                command.Parameters.Add(new SqlParameter("@account", SqlDbType.UniqueIdentifier) { Value = id });
                command.Parameters.Add(new SqlParameter("@date", SqlDbType.Date) { Value = filter.Cursor?.PostingDate?.ToDateTime(TimeOnly.MinValue) ?? (object)DBNull.Value });
                command.Parameters.Add(new SqlParameter("@sequence", SqlDbType.BigInt) { Value = (object?)filter.Cursor?.Sequence ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@ordinal", SqlDbType.Int) { Value = (object?)filter.Cursor?.Ordinal ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = (object?)filter.Cursor?.Id ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@limit", SqlDbType.Int) { Value = filter.PageSize + 1 });
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var scale = reader.GetInt32(7);
                    rows.Add(new(reader.GetGuid(0), reader.GetInt64(1), reader.GetGuid(2), reader.GetInt32(3),
                        Date(4, reader), reader.GetFieldValue<DateTimeOffset>(5), reader.GetString(6), scale,
                        reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11),
                        FormatSmall(reader.GetDecimal(12), scale), FormatSmall(reader.GetDecimal(13), scale)));
                }
            }
            var hasMore = rows.Count > filter.PageSize;
            if (hasMore) rows.RemoveAt(rows.Count - 1);
            var totals = await Totals(connection, sqlTx,
                "Accounting.JournalLines l JOIN Accounting.JournalEntries j ON j.TenantId=l.TenantId AND j.Id=l.JournalId",
                "l.TenantId=@tenant AND l.AccountId=@account AND j.PostingDate<=@posting AND j.RecordedAtUtc<=@recorded",
                "l.Debit", "l.Credit", tenant, filter.PostingThrough, recorded, ct, id);
            var pageTotals = SumSmall(rows.Select(row => (row.Debit, row.Credit)));
            var currency = await Currency(connection, sqlTx, tenant, ct);
            var last = rows.LastOrDefault();
            var next = hasMore && last is not null ? MakeCursor(protector,
                new CursorState(tenant, route, filter.PostingThrough, recorded, filter.PageSize,
                    last.Sequence, last.JournalId, last.PostingDate, last.Ordinal)) : null;
            await tx.CommitAsync(ct);
            return Results.Ok(new AccountJournalPage(id, rows, next, filter.PostingThrough, recorded,
                FormatTotals(totals, currency.Scale), FormatTotals(pageTotals, currency.Scale)));
        }
        catch (SqlException e) when (e.Number == 8115) { return Results.Problem(statusCode: 422, title: "Report totals exceed the supported range."); }
        catch (SqlException e) when (Retryable(e)) { return Retry(); }
    }

    internal static async Task<IResult> ReadTrialBalance(HttpContext http, WorkbenchDbContext database,
        IDataProtector protector, CancellationToken ct)
    {
        var tenant = database.TenantContext.RequireTenantId();
        if (!TryReadFilter(http, tenant, "trial-balance", protector, out var filter)) return InvalidFilter();
        try
        {
            await using var tx = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var connection = (SqlConnection)database.Database.GetDbConnection();
            var sqlTx = (SqlTransaction)tx.GetDbTransaction();
            var recorded = filter!.RecordedThrough ?? await DatabaseNow(connection, sqlTx, ct);
            var rows = new List<TrialBalanceAccount>();
            await using (var command = Command(connection, sqlTx, """
                SELECT l.AccountId,a.Code,a.Name,a.ArchivedAtUtc,
                    CONVERT(varchar(64),SUM(CONVERT(decimal(38,4),l.Debit))),
                    CONVERT(varchar(64),SUM(CONVERT(decimal(38,4),l.Credit))),
                    CONVERT(varchar(64),SUM(CONVERT(decimal(38,4),l.Debit-l.Credit)))
                FROM Accounting.JournalLines l
                JOIN Accounting.JournalEntries j ON j.TenantId=l.TenantId AND j.Id=l.JournalId
                JOIN Accounting.Accounts a ON a.TenantId=l.TenantId AND a.Id=l.AccountId
                WHERE l.TenantId=@tenant AND j.PostingDate<=@posting AND j.RecordedAtUtc<=@recorded
                    AND (@id IS NULL OR l.AccountId>@id)
                GROUP BY l.AccountId,a.Code,a.Name,a.ArchivedAtUtc
                ORDER BY l.AccountId OFFSET 0 ROWS FETCH NEXT @limit ROWS ONLY
                """, tenant, filter.PostingThrough, recorded))
            {
                command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = (object?)filter.Cursor?.Id ?? DBNull.Value });
                command.Parameters.Add(new SqlParameter("@limit", SqlDbType.Int) { Value = filter.PageSize + 1 });
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), !reader.IsDBNull(3),
                        reader.GetString(4), reader.GetString(5), reader.GetString(6)));
            }
            var hasMore = rows.Count > filter.PageSize;
            if (hasMore) rows.RemoveAt(rows.Count - 1);
            var totals = await Totals(connection, sqlTx,
                "Accounting.JournalLines l JOIN Accounting.JournalEntries j ON j.TenantId=l.TenantId AND j.Id=l.JournalId",
                "l.TenantId=@tenant AND j.PostingDate<=@posting AND j.RecordedAtUtc<=@recorded",
                "l.Debit", "l.Credit", tenant, filter.PostingThrough, recorded, ct);
            var pageTotals = await TrialPageTotals(connection, sqlTx, tenant, filter.PostingThrough, recorded,
                rows.Select(row => row.AccountId).ToArray(), ct);
            var currency = await Currency(connection, sqlTx, tenant, ct);
            rows = rows.Select(row => row with
            {
                DebitActivity = FormatWide(row.DebitActivity, currency.Scale),
                CreditActivity = FormatWide(row.CreditActivity, currency.Scale),
                DebitMinusCredit = FormatWide(row.DebitMinusCredit, currency.Scale)
            }).ToList();
            var last = rows.LastOrDefault();
            var next = hasMore && last is not null ? MakeCursor(protector,
                new CursorState(tenant, "trial-balance", filter.PostingThrough, recorded, filter.PageSize,
                    null, last.AccountId, null, null)) : null;
            await tx.CommitAsync(ct);
            return Results.Ok(new TrialBalancePage(rows, next, filter.PostingThrough, recorded,
                currency.Currency, currency.Scale, FormatTotals(totals, currency.Scale),
                FormatTotals(pageTotals, currency.Scale)));
        }
        catch (SqlException e) when (e.Number == 8115) { return Results.Problem(statusCode: 422, title: "Report totals exceed the supported range."); }
        catch (SqlException e) when (Retryable(e)) { return Retry(); }
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction tx, string sql,
        Guid tenant, DateOnly posting, DateTimeOffset recorded)
    {
        var command = new SqlCommand(sql, connection, tx) { CommandTimeout = 30 };
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = tenant });
        command.Parameters.Add(new SqlParameter("@posting", SqlDbType.Date) { Value = posting.ToDateTime(TimeOnly.MinValue) });
        command.Parameters.Add(new SqlParameter("@recorded", SqlDbType.DateTimeOffset) { Value = recorded });
        return command;
    }

    private static async Task<DateTimeOffset> DatabaseNow(SqlConnection connection, SqlTransaction tx, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT SYSDATETIMEOFFSET() AT TIME ZONE 'UTC'", connection, tx);
        return (DateTimeOffset)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<bool> AccountExists(SqlConnection connection, SqlTransaction tx,
        Guid tenant, Guid id, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT 1 FROM Accounting.Accounts WHERE TenantId=@tenant AND Id=@id", connection, tx);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = tenant });
        command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id });
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<ReportTotals> Totals(SqlConnection connection, SqlTransaction tx,
        string from, string where, string debit, string credit, Guid tenant, DateOnly posting,
        DateTimeOffset recorded, CancellationToken ct, Guid? account = null)
    {
        await using var command = Command(connection, tx, $"""
            SELECT CONVERT(varchar(64),COALESCE(SUM(CONVERT(decimal(38,4),{debit})),CONVERT(decimal(38,4),0))),
                CONVERT(varchar(64),COALESCE(SUM(CONVERT(decimal(38,4),{credit})),CONVERT(decimal(38,4),0)))
            FROM {from} WHERE {where}
            """, tenant, posting, recorded);
        if (account.HasValue)
            command.Parameters.Add(new SqlParameter("@account", SqlDbType.UniqueIdentifier) { Value = account.Value });
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Report totals are unavailable.");
        return new(reader.GetString(0), reader.GetString(1));
    }

    private static async Task<ReportTotals> TrialPageTotals(SqlConnection connection, SqlTransaction tx,
        Guid tenant, DateOnly posting, DateTimeOffset recorded, Guid[] accounts, CancellationToken ct)
    {
        if (accounts.Length == 0) return new("0.0000", "0.0000");
        // Page size is limited to 100, well below SQL Server's parameter limit.
        var ids = string.Join(',', accounts.Select((_, index) => $"@a{index}"));
        await using var command = Command(connection, tx, $"""
            SELECT CONVERT(varchar(64),SUM(CONVERT(decimal(38,4),l.Debit))),
                CONVERT(varchar(64),SUM(CONVERT(decimal(38,4),l.Credit)))
            FROM Accounting.JournalLines l
            JOIN Accounting.JournalEntries j ON j.TenantId=l.TenantId AND j.Id=l.JournalId
            WHERE l.TenantId=@tenant AND j.PostingDate<=@posting AND j.RecordedAtUtc<=@recorded
                AND l.AccountId IN ({ids})
            """, tenant, posting, recorded);
        for (var i = 0; i < accounts.Length; i++)
            command.Parameters.Add(new SqlParameter($"@a{i}", SqlDbType.UniqueIdentifier) { Value = accounts[i] });
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Trial balance page totals are unavailable.");
        return new(reader.GetString(0), reader.GetString(1));
    }

    private static async Task<(string? Currency, int Scale)> Currency(SqlConnection connection, SqlTransaction tx,
        Guid tenant, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT Currency,Scale FROM Accounting.PolicyFreezes WHERE TenantId=@tenant", connection, tx);
        command.Parameters.Add(new SqlParameter("@tenant", SqlDbType.UniqueIdentifier) { Value = tenant });
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetString(0), reader.GetInt32(1)) : (null, 4);
    }

    private static JournalHeader ReadHeader(SqlDataReader reader)
    {
        var scale = reader.GetInt32(9);
        return new(reader.GetGuid(0), reader.GetInt64(1), reader.GetGuid(2), reader.GetString(3),
            reader.GetGuid(4), reader.GetGuid(5), reader.GetString(6), reader.GetGuid(7),
            reader.GetString(8), scale, Date(10, reader), Date(11, reader), Date(12, reader),
            reader.GetFieldValue<DateTimeOffset>(13), reader.GetGuid(14), NullableString(15, reader),
            NullableString(16, reader), FormatSmall(reader.GetDecimal(17), scale),
            FormatSmall(reader.GetDecimal(18), scale));
    }

    private static DateOnly Date(int ordinal, SqlDataReader reader) =>
        DateOnly.FromDateTime(reader.GetDateTime(ordinal));
    private static string? NullableString(int ordinal, SqlDataReader reader) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static string FormatSmall(decimal value, int scale) => value.ToString($"F{scale}", CultureInfo.InvariantCulture);

    private static ReportTotals SumSmall(IEnumerable<(string Debit, string Credit)> values)
    {
        // Each page has at most 100 entries, but their combined value may exceed CLR decimal.
        // SQL computes authoritative whole-filter and page totals when such ranges matter.
        var debit = System.Numerics.BigInteger.Zero;
        var credit = System.Numerics.BigInteger.Zero;
        foreach (var value in values)
        {
            debit += FixedUnits(value.Debit);
            credit += FixedUnits(value.Credit);
        }
        return new(FormatUnits(debit), FormatUnits(credit));
    }

    private static System.Numerics.BigInteger FixedUnits(string value)
    {
        var parts = value.Split('.');
        return System.Numerics.BigInteger.Parse(parts[0], CultureInfo.InvariantCulture) * 10000 +
            System.Numerics.BigInteger.Parse((parts.Length == 1 ? "0" : parts[1]).PadRight(4, '0'), CultureInfo.InvariantCulture);
    }

    private static string FormatUnits(System.Numerics.BigInteger units)
    {
        var sign = units.Sign < 0 ? "-" : "";
        var absolute = System.Numerics.BigInteger.Abs(units);
        return $"{sign}{absolute / 10000}.{(absolute % 10000).ToString(CultureInfo.InvariantCulture).PadLeft(4, '0')}";
    }

    private static ReportTotals FormatTotals(ReportTotals totals, int scale) =>
        new(FormatWide(totals.Debit, scale), FormatWide(totals.Credit, scale));

    private static string FormatWide(string value, int scale)
    {
        if (scale is < 0 or > 4) throw new InvalidOperationException("Stored journal scale is invalid.");
        var parts = value.Split('.');
        if (parts.Length != 2 || parts[1].Length != 4 ||
            parts[1].AsSpan(scale).IndexOfAnyExcept('0') >= 0)
            throw new InvalidOperationException("Journal aggregate has precision beyond its stored scale.");
        return scale == 0 ? parts[0] : $"{parts[0]}.{parts[1][..scale]}";
    }

    private static bool Retryable(SqlException error) => error.Number is -2 or 1205 or 1222;
}
