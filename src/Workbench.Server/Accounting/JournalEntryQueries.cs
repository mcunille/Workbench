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

internal static partial class JournalReportQueries
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

    internal static async Task<IResult> ReadJournal(Guid id, WorkbenchDbContext database, Authorization.RequestActor actor, CancellationToken ct)
    {
        var tenant = database.TenantContext.RequireTenantId();
        try
        {
            await using var tx = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await FinancialEvidenceQueries.CoordinateReadAsync(database, ct);
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
            var corrections = await CorrectionsForJournal(id, database, ct);
            var recognition = await PurchaseRecognitionReports.ReadAsync(database, id, ct);
            var evidenceKind = source.SourceKind;
            var evidenceOwner = source.SourceId;
            var evidenceRevision = recognition?.SourceRevision ?? source.SourceRevision;
            if (recognition is not null)
            {
                // Bill-backed recognition shares the bill's evidence; standalone sides own evidence by event ID.
                var billBacked = await database.FinancialEvidenceSets.AsNoTracking().AnyAsync(
                    set => set.OwnerKind == "SupplierBill" && set.OwnerId == recognition.SourceId &&
                        set.OwnerRevisionId == recognition.SourceRevision, ct);
                evidenceKind = billBacked ? "SupplierBill" : "PurchaseRecognition";
                evidenceOwner = billBacked ? recognition.SourceId : recognition.EventId;
            }
            else
            {
                var original = await (from correction in database.JournalCorrectionGroups.AsNoTracking()
                                      join journal in database.JournalEntries.AsNoTracking() on correction.OriginalJournalId equals journal.Id
                                      join originalSource in database.JournalSourceEvents.AsNoTracking() on journal.SourceEventId equals originalSource.Id
                                      where correction.ReversalJournalId == id
                                      select new { originalSource.SourceKind, originalSource.SourceId, originalSource.SourceRevision }).SingleOrDefaultAsync(ct);
                if (original is not null)
                {
                    evidenceKind = original.SourceKind;
                    evidenceOwner = original.SourceId;
                    evidenceRevision = original.SourceRevision;
                }
            }
            var evidence = await FinancialEvidenceQueries.ReadAsync(database, actor, evidenceKind, evidenceOwner, evidenceRevision, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new JournalDetail(header, lines, source, corrections, recognition, evidence));
        }
        catch (SqlException e) when (e.Number == 50903) { return Results.Problem(statusCode: 403, title: "Current evidence access is required."); }
        catch (SqlException e) when (e.Number == 8115) { return Results.Problem(statusCode: 422, title: "Report totals exceed the supported range."); }
        catch (SqlException e) when (Retryable(e)) { return Retry(); }
    }

    private static async Task<IReadOnlyList<JournalCorrectionEvidence>> CorrectionsForJournal(
        Guid journalId, WorkbenchDbContext database, CancellationToken ct)
    {
        var groups = await database.JournalCorrectionGroups.AsNoTracking()
            .Where(group => group.OriginalJournalId == journalId ||
                group.ReversalJournalId == journalId || group.ReplacementJournalId == journalId)
            .OrderBy(group => group.RecordedAtUtc).ThenBy(group => group.Id)
            .ToListAsync(ct);
        return groups.Select(group => new JournalCorrectionEvidence(group.Id,
            group.OriginalJournalId == journalId ? "Original" :
                group.ReversalJournalId == journalId ? "Reversal" : "Replacement",
            group.OriginalJournalId, group.ReversalJournalId, group.ReplacementJournalId,
            group.Reason, group.PostingDate, group.RecordedAtUtc,
            group.EvidenceJson, Convert.ToHexString(group.EvidenceSha256))).ToArray();
    }

}
