// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class JournalCorrectionTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task PrivilegedMultilineOriginalIsInvertedLineForLine()
    {
        // GIVEN a typed synthetic source posted by a privileged fixture with four arbitrary lines and distinct dates.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var journal = controls.Journal;
        var source = await journal.CreateSourceAsync("300");
        await using var admin = new SqlConnection(journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        await new TenantContextProof(journal.ProofKey).ApplyAsync(admin, JournalTestContext.TenantId, default);
        var lines = JsonSerializer.Serialize(new[] {
            new { ordinal=1, accountId=journal.DebitAccountId, accountVersion=journal.DebitAccountVersion, debit="100", credit="0" },
            new { ordinal=2, accountId=journal.DebitAccountId, accountVersion=journal.DebitAccountVersion, debit="200", credit="0" },
            new { ordinal=3, accountId=journal.CreditAccountId, accountVersion=journal.CreditAccountVersion, debit="0", credit="75" },
            new { ordinal=4, accountId=journal.CreditAccountId, accountVersion=journal.CreditAccountVersion, debit="0", credit="225" }
        });
        await using var post = new SqlCommand("""
            BEGIN TRANSACTION;
            DECLARE @Result TABLE(SourceEventId uniqueidentifier,JournalId uniqueidentifier,Sequence bigint,RecordedAtUtc datetimeoffset);
            INSERT @Result EXEC Accounting.PostJournal @ActorId=@actor,@SessionId=@session,@RequestId=@request,
                @RequiredPermission=N'AccountingConfigurationManage',@SourceCommandKind=N'SyntheticPost',@SourceCommandVersion=1,
                @CanonicalInput=N'{}',@SourceKind=N'Synthetic',@SourceId=@source,@SourceRevision=@revision,
                @EventKind=N'Posted',@RuleVersion=1,@ExpectedConfigurationVersion=@config,@Currency=N'USD',
                @DocumentDate='2026-09-14',@EffectiveDate='2026-09-15',@PostingDate='2026-09-16',
                @SourceSnapshot=N'{"fixture":"multiline"}',@Lines=@lines;
            UPDATE Accounting.SyntheticSources SET FrozenAtUtc=SYSUTCDATETIME() WHERE Id=@source;
            COMMIT;
            SELECT JournalId FROM @Result;
            """, admin);
        post.Parameters.AddWithValue("@actor", JournalTestContext.ActorId);
        post.Parameters.AddWithValue("@session", journal.SessionId);
        post.Parameters.AddWithValue("@request", Guid.NewGuid());
        post.Parameters.AddWithValue("@source", source.Id);
        post.Parameters.AddWithValue("@revision", source.Revision);
        post.Parameters.AddWithValue("@config", journal.ConfigurationVersion);
        post.Parameters.AddWithValue("@lines", lines);
        var original = (Guid)(await post.ExecuteScalarAsync())!;
        // WHEN the arbitrary original is reversed by the restricted typed adapter.
        var result = await controls.CorrectAsync(original, new DateOnly(2026, 10, 1), null);
        // THEN every original ordinal, snapshot and exact amount survives, with only debit/credit swapped.
        await CorrectionAssertions.ExactInverseAsync(controls, original, result.ReversalJournalId);
        Assert.Equal(new DateTime(2026, 9, 14), await CorrectionAssertions.ScalarAsync(journal.Connection,
            "SELECT DocumentDate FROM Accounting.JournalEntries WHERE Id=@id", result.ReversalJournalId));
        Assert.Equal(new DateTime(2026, 9, 15), await CorrectionAssertions.ScalarAsync(journal.Connection,
            "SELECT EffectiveDate FROM Accounting.JournalEntries WHERE Id=@id", result.ReversalJournalId));
    }

    [Fact]
    public async Task TrustedKernelRejectsPartialReplacementsReusedRevisionsAndDuplicateCanonicalFields()
    {
        // GIVEN a valid posted original and trusted source-boundary access in a disposable database.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var source = await controls.Journal.CreateSourceAsync("300");
        var original = await controls.Journal.PostAsync(source);
        var before = await controls.HistorySnapshotAsync();
        // WHEN the trusted caller violates replacement completeness or reuses the old source revision.
        Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => CorrectionAssertions.KernelAsync(
            controls, original.JournalId, replacementRevision: Guid.NewGuid()))).Number);
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => CorrectionAssertions.KernelAsync(
            controls, original.JournalId, replacementRevision: source.Revision, completeReplacement: true))).Number);
        Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => CorrectionAssertions.KernelAsync(
            controls, original.JournalId, canonical: "{\"value\":1,\"value\":2}"))).Number);
        Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => CorrectionAssertions.KernelAsync(
            controls, original.JournalId, canonical: "{\"value\":\"" + new string('x', 131072) + "\"}"))).Number);
        // THEN the entire history remains byte-for-byte unchanged.
        Assert.Equal(before, await controls.HistorySnapshotAsync());
    }

    [Fact]
    public async Task EveryCorrectionComponentHasOneDatabaseOwnedRecordedTime()
    {
        // GIVEN a posted source that will be corrected with a replacement.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var original = await controls.Journal.PostAsync(await controls.Journal.CreateSourceAsync("300"));
        // WHEN the whole correction commits.
        var result = await controls.CorrectAsync(original.JournalId, new DateOnly(2026, 10, 1), "280");
        // THEN any recorded-time cutoff includes either all correction entries or none of them.
        foreach (var id in new[] { result.ReversalJournalId, result.ReplacementJournalId!.Value })
            foreach (var sql in new[] {
                "SELECT RecordedAtUtc FROM Accounting.JournalEntries WHERE Id=@id",
                "SELECT e.RecordedAtUtc FROM Accounting.SourceEvents e JOIN Accounting.JournalEntries j ON j.TenantId=e.TenantId AND j.SourceEventId=e.Id WHERE j.Id=@id",
                "SELECT RecordedAtUtc FROM Accounting.PostingReceipts WHERE JournalId=@id" })
                Assert.Equal(result.RecordedAtUtc, await CorrectionAssertions.ScalarAsync(controls.Journal.Connection, sql, id));
        Assert.Equal(original.RecordedAtUtc, await CorrectionAssertions.ScalarAsync(controls.Journal.Connection,
            "SELECT RecordedAtUtc FROM Accounting.JournalEntries WHERE Id=@id", original.JournalId));
        Assert.Equal(original.RecordedAtUtc, await CorrectionAssertions.ScalarAsync(controls.Journal.Connection,
            "SELECT RecordedAtUtc FROM Accounting.SourceEvents WHERE Id=@id", original.SourceEventId));
        Assert.Equal(original.RecordedAtUtc, await CorrectionAssertions.ScalarAsync(controls.Journal.Connection,
            "SELECT RecordedAtUtc FROM Accounting.PostingReceipts WHERE JournalId=@id", original.JournalId));
    }

    [Theory]
    [InlineData(0, "999999999999999999999999")]
    [InlineData(1, "999999999999999999999999.9")]
    [InlineData(2, "999999999999999999999999.99")]
    [InlineData(3, "999999999999999999999999.999")]
    [InlineData(4, "999999999999999999999999.9999")]
    public async Task InverseRetainsMaximumExactValuesAtEveryScale(int scale, string amount)
    {
        // GIVEN the largest legal amount for each configured scale.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        await controls.Journal.ConfigureAsync(scale: scale);
        var posted = await controls.Journal.PostAsync(await controls.Journal.CreateSourceAsync(amount));
        // WHEN the original is reversed without a replacement.
        var result = await controls.CorrectAsync(posted.JournalId, new DateOnly(2026, 10, 1), null);
        // THEN exact persisted amounts and all account snapshots are inverted without rounding.
        Assert.Null(result.ReplacementJournalId);
        Assert.Null(result.ReplacementSourceRevision);
        await CorrectionAssertions.ExactInverseAsync(controls, posted.JournalId, result.ReversalJournalId);
        Assert.Equal(2, await controls.Journal.CountAsync("JournalEntries"));
    }

    [Fact]
    public async Task RenamedAccountsUseHistoricalInverseAndCurrentReplacementWithPreservedDates()
    {
        // GIVEN a September posting followed by a permitted account rename and September closure.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var journal = controls.Journal;
        var source = await journal.CreateSourceAsync("300");
        var posted = await journal.PostAsync(source, postingDate: new DateTime(2026, 9, 15));
        var originalConfigurationVersion = journal.ConfigurationVersion;
        await journal.SaveAsync(Guid.NewGuid(), "UpdateAccount",
            """{"code":"1911","name":"Renamed debit","description":"Current label"}""",
            journal.DebitAccountId, journal.DebitAccountVersion);
        await journal.ConfigureAsync();
        await controls.CloseAsync(new DateOnly(2026, 9, 1));
        // WHEN replacement corrects the document date in an open October period.
        var corrected = await controls.CorrectAsync(posted.JournalId, new DateOnly(2026, 10, 2), "280",
            replacementDocumentDate: new DateOnly(2026, 9, 16));
        // THEN the inverse is historical and the replacement uses current account snapshots.
        await CorrectionAssertions.ExactInverseAsync(controls, posted.JournalId, corrected.ReversalJournalId);
        Assert.Equal("Renamed debit", await CorrectionAssertions.ScalarAsync(journal.Connection,
            "SELECT AccountName FROM Accounting.JournalLines WHERE JournalId=@id AND Ordinal=1", corrected.ReplacementJournalId));
        Assert.Equal(new DateTime(2026, 9, 15), await CorrectionAssertions.ScalarAsync(journal.Connection,
            "SELECT DocumentDate FROM Accounting.JournalEntries WHERE Id=@id", corrected.ReversalJournalId));
        Assert.Equal(new DateTime(2026, 9, 16), await CorrectionAssertions.ScalarAsync(journal.Connection,
            "SELECT DocumentDate FROM Accounting.JournalEntries WHERE Id=@id", corrected.ReplacementJournalId));
        Assert.Equal(new DateTime(2026, 9, 15), await CorrectionAssertions.ScalarAsync(journal.Connection,
            "SELECT EffectiveDate FROM Accounting.JournalEntries WHERE Id=@id", corrected.ReplacementJournalId));
        Assert.Equal(originalConfigurationVersion, await CorrectionAssertions.ScalarAsync(journal.Connection,
            "SELECT ConfigurationVersion FROM Accounting.JournalEntries WHERE Id=@id", corrected.ReversalJournalId));
        Assert.Equal(journal.ConfigurationVersion, await CorrectionAssertions.ScalarAsync(journal.Connection,
            "SELECT ConfigurationVersion FROM Accounting.JournalEntries WHERE Id=@id", corrected.ReplacementJournalId));
        Assert.Equal(3, await journal.CountAsync("SyntheticSourceRevisions"));
        Assert.Equal(300m, await CorrectionAssertions.ScalarAsync(journal.Connection,
            "SELECT Amount FROM Accounting.SyntheticSourceRevisions WHERE Revision=@id", source.Revision));
        // AND the retained evidence hash covers exactly SQL's UTF-16LE string bytes.
        var evidence = (string)(await CorrectionAssertions.ScalarAsync(journal.Connection,
            "SELECT EvidenceJson FROM Accounting.CorrectionGroups WHERE Id=@id", corrected.CorrectionId))!;
        Assert.Equal(SHA256.HashData(Encoding.Unicode.GetBytes(evidence)),
            (byte[])(await CorrectionAssertions.ScalarAsync(journal.Connection,
                "SELECT EvidenceSha256 FROM Accounting.CorrectionGroups WHERE Id=@id", corrected.CorrectionId))!);
    }

    [Fact]
    public async Task ReplacementCanBeCorrectedButAnOriginalAndReversalCannotBeReversedAgain()
    {
        // GIVEN an original followed by a complete replacement.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var original = await controls.Journal.PostAsync(await controls.Journal.CreateSourceAsync("300"));
        var first = await controls.CorrectAsync(original.JournalId, new DateOnly(2026, 10, 1), "280");
        // WHEN the replacement is corrected again, the chain extends by a fresh group.
        var second = await controls.CorrectAsync(first.ReplacementJournalId!.Value, new DateOnly(2026, 10, 2), "260");
        // THEN five immutable journals remain, and neither earlier original nor inverse may be reversed again.
        Assert.Equal(5, await controls.Journal.CountAsync("JournalEntries"));
        Assert.Equal(2, await controls.Journal.CountAsync("CorrectionGroups"));
        Assert.NotEqual(first.ReplacementSourceRevision, second.ReplacementSourceRevision);
        foreach (var forbidden in new[] { original.JournalId, first.ReversalJournalId })
            await Assert.ThrowsAsync<SqlException>(() => controls.CorrectAsync(forbidden, new DateOnly(2026, 10, 3), null));
        Assert.Equal(5, await controls.Journal.CountAsync("JournalEntries"));
    }

    [Fact]
    public async Task InvalidReasonsEvidenceStaleExpectationsAndUnsupportedSourcesLeaveNoChanges()
    {
        // GIVEN one current posted source and a byte snapshot of its history.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var journal = controls.Journal;
        var original = await journal.PostAsync(await journal.CreateSourceAsync("300"));
        var before = await controls.HistorySnapshotAsync();
        // WHEN commands contain blank/oversized reasons, malformed evidence, stale versions or an unknown original.
        foreach (var reason in new[] { "", " \t\r\n", "\u2003", new string('x', 2001) })
            Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => controls.CorrectAsync(
                original.JournalId, new DateOnly(2026, 10, 1), "280", reason: reason))).Number);
        foreach (var evidence in new[] { "{}", "[]", "{\"schemaVersion\":1,\"schemaVersion\":1}", "{\"unknown\":true}",
            "{\"schemaVersion\":1,\"originalSourceRevision\":\"" + new string('x', 131072) + "\"}" })
            Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => controls.CorrectAsync(
                original.JournalId, new DateOnly(2026, 10, 1), "280", evidenceJson: evidence))).Number);
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => controls.CorrectAsync(
            original.JournalId, new DateOnly(2026, 10, 1), "280", expectedSourceRevision: Guid.NewGuid()))).Number);
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => controls.CorrectAsync(
            original.JournalId, new DateOnly(2026, 10, 1), "280", expectedConfigurationVersion: Guid.NewGuid()))).Number);
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => controls.CorrectAsync(
            original.JournalId, new DateOnly(2026, 10, 1), "280", expectedDebitVersion: Guid.NewGuid()))).Number);
        Assert.Equal(51004, (await Assert.ThrowsAsync<SqlException>(() => controls.CorrectAsync(
            Guid.NewGuid(), new DateOnly(2026, 10, 1), null))).Number);
        // THEN every rejected command leaves identical durable history.
        Assert.Equal(before, await controls.HistorySnapshotAsync());
        await CorrectionAssertions.AdminAsync(journal, "UPDATE Accounting.SyntheticSources SET HasDependencies=1");
        Assert.Equal(51000, (await Assert.ThrowsAsync<SqlException>(() => controls.CorrectAsync(
            original.JournalId, new DateOnly(2026, 10, 1), "280"))).Number);
        Assert.Equal(before, await controls.HistorySnapshotAsync());
        await CorrectionAssertions.AdminAsync(journal, "UPDATE Accounting.SourceEvents SET SourceKind=N'Unsupported'");
        Assert.Equal(51004, (await Assert.ThrowsAsync<SqlException>(() => controls.CorrectAsync(
            original.JournalId, new DateOnly(2026, 10, 1), "280"))).Number);
    }

    [Fact]
    public async Task ClosedPostingDatesRejectBothCorrectionShapesButCommittedReceiptsReplayAfterClosure()
    {
        // GIVEN a posted original and an explicitly closed October period.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var original = await controls.Journal.PostAsync(await controls.Journal.CreateSourceAsync("300"));
        await controls.CloseAsync(new DateOnly(2026, 10, 1));
        var before = await controls.HistorySnapshotAsync();
        // WHEN either correction shape targets that closed month.
        foreach (var replacement in new string?[] { null, "280" })
            Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => controls.CorrectAsync(
                original.JournalId, new DateOnly(2026, 10, 1), replacement))).Number);
        // THEN neither shape changed history, and a committed November command replays after November closes.
        Assert.Equal(before, await controls.HistorySnapshotAsync());
        var request = Guid.NewGuid();
        var result = await controls.CorrectAsync(original.JournalId, new DateOnly(2026, 11, 1), "280", request);
        await controls.CloseAsync(new DateOnly(2026, 11, 1));
        Assert.Equal(result, await controls.CorrectAsync(original.JournalId, new DateOnly(2026, 11, 1), "280", request));
        Assert.Equal(51009, (await Assert.ThrowsAsync<SqlException>(() => controls.CorrectAsync(
            original.JournalId, new DateOnly(2026, 11, 1), "281", request))).Number);
    }

    [Fact]
    public async Task CorrectionAppendsExactInverseAndReplacementAndReplaysWholeResult()
    {
        // GIVEN a posted independent synthetic source.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var source = await controls.Journal.CreateSourceAsync("300.00");
        var posted = await controls.Journal.PostAsync(source);
        var originalSnapshot = await CorrectionAssertions.ScalarAsync(controls.Journal.Connection,
            "SELECT SnapshotJson FROM Accounting.SourceEvents WHERE Id=@id", posted.SourceEventId);
        var request = Guid.NewGuid();
        // WHEN its amount is corrected and the identical command is retried.
        var result = await controls.CorrectAsync(posted.JournalId, new DateOnly(2026, 10, 1), "280.00", request);
        var replay = await controls.CorrectAsync(posted.JournalId, new DateOnly(2026, 10, 1), "280.00", request);
        // THEN one complete group owns exactly three journals and a new source revision.
        Assert.Equal(result, replay);
        Assert.NotEqual(source.Revision, result.ReplacementSourceRevision);
        Assert.Equal(3, await controls.Journal.CountAsync("JournalEntries"));
        Assert.Equal(1, await controls.Journal.CountAsync("CorrectionGroups"));
        Assert.Equal(originalSnapshot, await CorrectionAssertions.ScalarAsync(controls.Journal.Connection,
            "SELECT SnapshotJson FROM Accounting.SourceEvents WHERE Id=@id", posted.SourceEventId));
        Assert.Equal(originalSnapshot, await CorrectionAssertions.ScalarAsync(controls.Journal.Connection,
            "SELECT e.SnapshotJson FROM Accounting.SourceEvents e JOIN Accounting.JournalEntries j ON j.TenantId=e.TenantId AND j.SourceEventId=e.Id WHERE j.Id=@id", result.ReversalJournalId));
        Assert.Equal(result.ReplacementSourceRevision, await CorrectionAssertions.ScalarAsync(controls.Journal.Connection,
            "SELECT Revision FROM Accounting.SyntheticSources WHERE Id=@id", source.Id));
        Assert.Equal(1, await CorrectionAssertions.ScalarAsync(controls.Journal.Connection,
            """
            SELECT COUNT(*) FROM Accounting.SyntheticSourceRevisions r
            JOIN Accounting.SourceEvents e ON e.TenantId=r.TenantId AND e.SourceId=r.SourceId AND e.SourceRevision=r.Revision
            JOIN Accounting.JournalEntries j ON j.TenantId=e.TenantId AND j.SourceEventId=e.Id WHERE j.Id=@id
            """, result.ReversalJournalId));
        Assert.Equal(300m, await CorrectionAssertions.ScalarAsync(controls.Journal.Connection,
            "SELECT DebitTotal FROM Accounting.JournalEntries WHERE Id=@id", posted.JournalId));
        Assert.Equal(280m, await CorrectionAssertions.ScalarAsync(controls.Journal.Connection,
            "SELECT DebitTotal FROM Accounting.JournalEntries WHERE Id=@id", result.ReplacementJournalId));
        await using var command = new SqlCommand("""
            SELECT COUNT(*) FROM Accounting.JournalLines o
            JOIN Accounting.JournalLines r ON r.TenantId=o.TenantId AND r.Ordinal=o.Ordinal
            WHERE o.JournalId=@original AND r.JournalId=@reversal AND o.AccountId=r.AccountId
              AND o.AccountVersion=r.AccountVersion AND o.AccountCode=r.AccountCode
              AND o.AccountName=r.AccountName AND o.AccountType=r.AccountType
              AND o.AccountPurpose=r.AccountPurpose AND o.Debit=r.Credit AND o.Credit=r.Debit
            """, controls.Journal.Connection);
        command.Parameters.AddWithValue("@original", posted.JournalId);
        command.Parameters.AddWithValue("@reversal", result.ReversalJournalId);
        Assert.Equal(2, await command.ExecuteScalarAsync());
    }
}

internal static class CorrectionAssertions
{
    internal static async Task<Guid> KernelAsync(JournalControlTestContext controls, Guid original,
        Guid? replacementRevision = null, bool completeReplacement = false, string canonical = "{}",
        Guid? request = null, Guid? actor = null, Guid? session = null)
    {
        var journal = controls.Journal;
        await using var admin = new SqlConnection(journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        await new TenantContextProof(journal.ProofKey).ApplyAsync(admin, JournalTestContext.TenantId, default);
        await using var command = new SqlCommand("""
            BEGIN TRY
              BEGIN TRANSACTION;
              DECLARE @Evidence nvarchar(max)=(SELECT 1 schemaVersion,CONVERT(nvarchar(36),e.SourceRevision) originalSourceRevision,
                CONVERT(varchar(64),e.SnapshotSha256,2) originalSnapshotSha256 FROM Accounting.SourceEvents e
                JOIN Accounting.JournalEntries j ON j.TenantId=e.TenantId AND j.SourceEventId=e.Id
                WHERE j.Id=@original AND j.TenantId=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'))
                FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
              EXEC Accounting.CorrectJournal @ActorId=@actor,@SessionId=@session,@RequestId=@request,
                @RequiredPermission=N'AccountingConfigurationManage',@SourceCommandKind=N'TrustedSynthetic.Correct',@SourceCommandVersion=1,
                @CanonicalInput=@canonical,@OriginalJournalId=@original,@ExpectedConfigurationVersion=@config,
                @PostingDate='2026-10-01',@Reason=N'Trusted fixture correction',@Evidence=@Evidence,
                @ReplacementSourceRevision=@revision,@ReplacementDocumentDate=@document,@ReplacementRuleVersion=@rule,
                @ReplacementSnapshot=@snapshot,@ReplacementLines=@lines;
              COMMIT;
            END TRY
            BEGIN CATCH
              IF @@TRANCOUNT>0 ROLLBACK;
              THROW;
            END CATCH;
            """, admin);
        command.Parameters.AddWithValue("@actor", actor ?? JournalTestContext.ActorId);
        command.Parameters.AddWithValue("@session", session ?? journal.SessionId);
        command.Parameters.AddWithValue("@request", request ?? Guid.NewGuid());
        command.Parameters.AddWithValue("@original", original);
        command.Parameters.AddWithValue("@config", journal.ConfigurationVersion);
        command.Parameters.AddWithValue("@canonical", canonical);
        command.Parameters.AddWithValue("@revision", (object?)replacementRevision ?? DBNull.Value);
        command.Parameters.Add(new SqlParameter("@document", System.Data.SqlDbType.Date) { Value = completeReplacement ? new DateTime(2026, 9, 1) : DBNull.Value });
        command.Parameters.AddWithValue("@rule", completeReplacement ? 1 : DBNull.Value);
        command.Parameters.AddWithValue("@snapshot", completeReplacement ? "{}" : DBNull.Value);
        command.Parameters.AddWithValue("@lines", completeReplacement ? "[]" : DBNull.Value);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    internal static async Task<object?> ScalarAsync(SqlConnection connection, string sql, Guid? id = null)
    {
        await using var command = new SqlCommand(sql, connection);
        if (sql.Contains("@id", StringComparison.Ordinal))
            command.Parameters.AddWithValue("@id", (object?)id ?? DBNull.Value);
        return await command.ExecuteScalarAsync();
    }

    internal static async Task AdminAsync(JournalTestContext journal, string sql)
    {
        await using var admin = new SqlConnection(journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        await using var command = new SqlCommand(sql, admin);
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task ExactInverseAsync(JournalControlTestContext controls, Guid original, Guid reversal)
    {
        var columns = "Ordinal,AccountId,AccountVersion,AccountCode,AccountName,AccountType,AccountPurpose,";
        var originalLines = await ReadLinesAsync(
            "SELECT " + columns + "Credit Debit,Debit Credit FROM Accounting.JournalLines WHERE JournalId=@id ORDER BY Ordinal FOR JSON PATH", original);
        var inverseLines = await ReadLinesAsync(
            "SELECT " + columns + "Debit,Credit FROM Accounting.JournalLines WHERE JournalId=@id ORDER BY Ordinal FOR JSON PATH", reversal);
        Assert.Equal(originalLines, inverseLines);

        async Task<string> ReadLinesAsync(string sql, Guid id)
        {
            await using var command = new SqlCommand(sql, controls.Journal.Connection);
            command.Parameters.AddWithValue("@id", id);
            return await JournalControlTestContext.ReadCompleteJsonAsync(command);
        }
    }
}
