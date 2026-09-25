// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class AccountingPeriodTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task ClosedMonthRejectsNewPostingButReplaysOriginalRequest()
    {
        // GIVEN an existing posting and its durable request identity.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var journal = controls.Journal;
        var source = await journal.CreateSourceAsync();
        var request = Guid.NewGuid();
        var original = await journal.PostAsync(source, request, postingDate: new DateTime(2026, 9, 15));
        // WHEN the month closes and another source attempts to post.
        await controls.CloseAsync(new DateOnly(2026, 9, 1));
        var another = await journal.CreateSourceAsync();
        var rejected = await Assert.ThrowsAsync<SqlException>(() => journal.PostAsync(
            another, postingDate: new DateTime(2026, 9, 16)));
        // THEN no new journal exists, but the successful request still replays.
        Assert.Equal(51009, rejected.Number);
        Assert.Equal(1, await journal.CountAsync("JournalEntries"));
        Assert.Equal(original, await journal.PostAsync(source, request, postingDate: new DateTime(2026, 9, 15)));
    }

    [Fact]
    public async Task EmptyMonthCloseFreezesCalendarWithoutInventingFirstJournal()
    {
        // GIVEN configured accounting with no financial entries.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var journal = controls.Journal;
        Assert.Equal(0, await journal.CountAsync("JournalEntries"));
        // WHEN an empty month closes and the administrator tries to alter the currency.
        var close = await controls.CloseAsync(new DateOnly(2026, 9, 1));
        const string changedCurrency = """{"policies":{"country":"US","region":"CA","currency":"CAD","scale":2,"fiscalStartMonth":1,"startApproach":"OpeningBalances","plannedStartDate":"2026-01-01"},"mappings":[],"coverage":[]}""";
        var rejected = await Assert.ThrowsAsync<SqlException>(() => journal.SaveAsync(
            Guid.NewGuid(), "Configure", changedCurrency, expectedVersion: journal.ConfigurationVersion));
        // THEN the period retains its closure while first-journal freeze remains absent.
        Assert.Equal(50909, rejected.Number);
        Assert.Equal(0, await journal.CountAsync("PolicyFreezes"));
        Assert.Equal(1, await journal.CountAsync("Periods"));
        Assert.Equal(1, await journal.CountAsync("PeriodClosures"));
        Assert.NotEqual(Guid.Empty, close.ClosureId);
        // AND a descriptive region edit remains allowed.
        const string changedRegion = """{"policies":{"country":"US","region":"WA","currency":"USD","scale":2,"fiscalStartMonth":1,"startApproach":"OpeningBalances","plannedStartDate":"2026-01-01"},"mappings":[],"coverage":[]}""";
        await journal.SaveAsync(Guid.NewGuid(), "Configure", changedRegion,
            expectedVersion: journal.ConfigurationVersion);
    }

    [Fact]
    public async Task CloseRetryReplaysButDistinctRequestConflicts()
    {
        // GIVEN a close request with a durable request identity.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var request = Guid.NewGuid();
        var closed = await controls.CloseAsync(new DateOnly(2026, 9, 1), request);
        // WHEN the request is retried and another request targets the closed month.
        var replay = await controls.CloseAsync(new DateOnly(2026, 9, 1), request);
        var conflict = await Assert.ThrowsAsync<SqlException>(() =>
            controls.CloseAsync(new DateOnly(2026, 9, 1)));
        // THEN only the first closure and receipt exist.
        Assert.Equal(closed, replay);
        Assert.Equal(51009, conflict.Number);
        Assert.Equal(1, await controls.Journal.CountAsync("PeriodCloseReceipts"));
    }

    [Fact]
    public async Task RevokedPermissionPreventsCloseReceiptReplay()
    {
        // GIVEN a successful close and its request identity.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var request = Guid.NewGuid();
        await controls.CloseAsync(new DateOnly(2026, 9, 1), request);
        // WHEN the actor's accounting role is revoked before replay.
        await using var admin = new SqlConnection(controls.Journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        await using (var revoke = new SqlCommand("DELETE FROM [Identity].[UserRoles] WHERE TenantId=@tenant AND UserId=@actor", admin))
        {
            revoke.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
            revoke.Parameters.AddWithValue("@actor", JournalTestContext.ActorId);
            await revoke.ExecuteNonQueryAsync();
        }
        // THEN the receipt does not confer authority.
        var denied = await Assert.ThrowsAsync<SqlException>(() =>
            controls.CloseAsync(new DateOnly(2026, 9, 1), request));
        Assert.Equal(51003, denied.Number);
    }

    [Theory]
    [InlineData(1, "2026-01-01", "2028-02-29", "2028-02-01", "2028-02-29", "2028-01-01")]
    [InlineData(4, "2026-01-01", "2028-03-15", "2028-03-01", "2028-03-31", "2027-04-01")]
    [InlineData(1, "2026-01-15", "2026-01-15", "2026-01-01", "2026-01-31", "2026-01-01")]
    [InlineData(1, "2026-01-01", "9999-12-31", "9999-12-01", "9999-12-31", "9999-01-01")]
    public async Task PostingMaterializesExactCalendar(int fiscalMonth, string startDate, string postingDate,
        string expectedStart, string expectedEnd, string expectedFiscalStart)
    {
        // GIVEN a configured fiscal month and a valid posting date.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer, fiscalMonth, startDate);
        var journal = controls.Journal;
        var source = await journal.CreateSourceAsync();
        // WHEN the journal is posted.
        await journal.PostAsync(source, postingDate: DateTime.Parse(postingDate, System.Globalization.CultureInfo.InvariantCulture));
        // THEN the materialized month has hand-checked inclusive and fiscal boundaries.
        await using var command = new SqlCommand("SELECT PeriodStart,PeriodEnd,FiscalYearStart FROM Accounting.Periods", journal.Connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(expectedStart, reader.GetDateTime(0).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(expectedEnd, reader.GetDateTime(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(expectedFiscalStart, reader.GetDateTime(2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task UnrepresentableFiscalYearLeavesNoPeriodOrJournal()
    {
        // GIVEN a configured April fiscal year and a date in year one's preceding March.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer, fiscalStartMonth: 4, startDate: "0001-01-01");
        var source = await controls.Journal.CreateSourceAsync();
        // WHEN a posting attempts to derive year zero.
        var rejected = await Assert.ThrowsAsync<SqlException>(() => controls.Journal.PostAsync(source,
            postingDate: new DateTime(1, 3, 1)));
        // THEN the unsupported boundary leaves no financial or period rows.
        Assert.Equal(51000, rejected.Number);
        Assert.Equal(0, await controls.Journal.CountAsync("Periods"));
        Assert.Equal(0, await controls.Journal.CountAsync("JournalEntries"));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"kind\":\"SyntheticReconciliation\",\"periodStart\":\"2026-09-01\",\"unexpected\":true}")]
    [InlineData("{\"schemaVersion\":1,\"kind\":\"SyntheticReconciliation\",\"kind\":\"Other\",\"periodStart\":\"2026-09-01\"}")]
    [InlineData("{\"schemaVersion\":1,\"kind\":\"SyntheticReconciliation\",\"periodStart\":\"2026-10-01\"}")]
    public async Task CloseRejectsNoncanonicalEvidenceEnvelope(string evidence)
    {
        // GIVEN configured accounting and evidence with an unknown, duplicated or mismatched field.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        // WHEN the typed disposable adapter submits that evidence.
        var rejected = await Assert.ThrowsAsync<SqlException>(() =>
            controls.CloseAsync(new DateOnly(2026, 9, 1), evidenceJson: evidence));
        // THEN the close and receipt are absent.
        Assert.Equal(51000, rejected.Number);
        Assert.Equal(0, await controls.Journal.CountAsync("PeriodClosures"));
        Assert.Equal(0, await controls.Journal.CountAsync("PeriodCloseReceipts"));
    }

    [Fact]
    public async Task CloseRejectsWhitespaceOnlyReasonAndNonMonthStart()
    {
        // GIVEN configured accounting without a closed month.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var before = await controls.HistorySnapshotAsync();
        // WHEN a close has only non-space whitespace or a day after the first.
        var whitespace = await Assert.ThrowsAsync<SqlException>(() =>
            controls.CloseAsync(new DateOnly(2026, 9, 1), reason: "\t\n"));
        var midMonth = await Assert.ThrowsAsync<SqlException>(() =>
            controls.CloseAsync(new DateOnly(2026, 9, 2)));
        // THEN both commands are rejected without materializing a month.
        Assert.Equal(51000, whitespace.Number);
        Assert.Equal(51000, midMonth.Number);
        Assert.Equal(before, await controls.HistorySnapshotAsync());
    }

    [Fact]
    public async Task FailedPostingRollsBackNewPeriodMaterialization()
    {
        // GIVEN a scale-two configuration and a source amount with a third fractional digit.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var source = await controls.Journal.CreateSourceAsync(amount: "12.3450");
        var before = await controls.HistorySnapshotAsync();
        // WHEN the posting is rejected after its period check.
        var rejected = await Assert.ThrowsAsync<SqlException>(() => controls.Journal.PostAsync(source,
            postingDate: new DateTime(2026, 9, 15)));
        // THEN period and financial history are byte-identical to the initial state.
        Assert.Equal(51000, rejected.Number);
        Assert.Equal(before, await controls.HistorySnapshotAsync());
    }

    [Fact]
    public async Task PeriodConstraintRejectsAStaleFiscalYearBoundary()
    {
        // GIVEN an April fiscal policy and a proposed September month with a fiscal start one year too old.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer, fiscalStartMonth: 4);
        await using var admin = new SqlConnection(controls.Journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        await using var invalid = new SqlCommand("""
            INSERT Accounting.Periods(TenantId,PeriodStart,PeriodEnd,FiscalYearStart,ConfigurationVersion,
              Currency,Scale,FiscalStartMonth,StartApproach,AccountingStartDate,CreatedAtUtc)
            VALUES(@tenant,'2026-09-01','2026-09-30','2025-04-01',@version,
              'USD',2,4,N'OpeningBalances','2026-01-01',SYSUTCDATETIME());
            """, admin);
        invalid.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
        invalid.Parameters.AddWithValue("@version", controls.Journal.ConfigurationVersion);
        // WHEN SQL validates the immutable period row.
        var rejected = await Assert.ThrowsAsync<SqlException>(() => invalid.ExecuteNonQueryAsync());
        // THEN the calendar constraint rejects a year that does not own that month.
        Assert.Equal(547, rejected.Number);
    }

    [Fact]
    public async Task ReceiptCannotLinkClosureToAnotherMonthOfSameTenant()
    {
        // GIVEN a September closure and a distinct materialized October month.
        await using var controls = await JournalControlTestContext.OpenAsync(sqlServer);
        var closure = await controls.CloseAsync(new DateOnly(2026, 9, 1));
        var source = await controls.Journal.CreateSourceAsync();
        await controls.Journal.PostAsync(source, postingDate: new DateTime(2026, 10, 15));
        await using var admin = new SqlConnection(controls.Journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        await using var invalid = new SqlCommand("""
            INSERT Accounting.PeriodCloseReceipts(TenantId,RequestId,ActorId,CommandKind,CommandVersion,
              CanonicalInput,InputSha256,ClosureId,PeriodStart,RecordedAtUtc)
            VALUES(@tenant,NEWID(),@actor,N'Period.Close',1,N'{}',0x0000000000000000000000000000000000000000000000000000000000000000,
              @closure,'2026-10-01',SYSUTCDATETIME());
            """, admin);
        invalid.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
        invalid.Parameters.AddWithValue("@actor", JournalTestContext.ActorId);
        invalid.Parameters.AddWithValue("@closure", closure.ClosureId);
        // WHEN a receipt claims that September's closure belongs to October.
        var rejected = await Assert.ThrowsAsync<SqlException>(() => invalid.ExecuteNonQueryAsync());
        // THEN the tenant-and-month-qualified relationship blocks the mismatch.
        Assert.Equal(547, rejected.Number);
    }
}
