// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using System.Text.Json;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class AccountingPeriodMigrationTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task UpgradeBackfillsOnlyPostedMonthsAndPreservesJournalAndReplayBytes()
    {
        // GIVEN a BK-02 database with two successful months and durable requests.
        await using var journal = await JournalTestContext.OpenAsync(sqlServer, priorMigration: "AddAtomicJournal");
        await journal.CreateGeneralAccountsAsync();
        await journal.ConfigureAsync(fiscalStartMonth: 4);
        var feb = await journal.CreateSourceAsync();
        var apr = await journal.CreateSourceAsync();
        var febRequest = Guid.NewGuid();
        var febResult = await journal.PostAsync(feb, febRequest, postingDate: new DateTime(2028, 2, 29));
        await journal.PostAsync(apr, postingDate: new DateTime(2028, 4, 1));
        var before = await SnapshotAsync();
        using (var history = JsonDocument.Parse(before))
            Assert.Equal(2, history.RootElement[0].GetProperty("JournalEntries").GetArrayLength());
        // WHEN the period release migration upgrades the database.
        await DatabaseMigrator.MigrateAsync(journal.Application.AdminConnectionString, default);
        // THEN historical financial bytes and replay results remain unchanged.
        Assert.Equal(before, await SnapshotAsync());
        Assert.Equal(febResult, await journal.PostAsync(feb, febRequest, postingDate: new DateTime(2028, 2, 29)));
        await using var admin = new SqlConnection(journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        await using var periods = new SqlCommand("""
            SELECT CONVERT(varchar(10),PeriodStart,23),CONVERT(varchar(10),PeriodEnd,23),
              CONVERT(varchar(10),FiscalYearStart,23) FROM Accounting.Periods ORDER BY PeriodStart;
            """, admin);
        await using var reader = await periods.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("2028-02-01", reader.GetString(0));
        Assert.Equal("2028-02-29", reader.GetString(1));
        Assert.Equal("2027-04-01", reader.GetString(2));
        Assert.True(await reader.ReadAsync());
        Assert.Equal("2028-04-01", reader.GetString(0));
        Assert.Equal("2028-04-30", reader.GetString(1));
        Assert.Equal("2028-04-01", reader.GetString(2));
        Assert.False(await reader.ReadAsync());
        await MigrationHistoryAssertions.AssertCurrentAsync(journal.Application.AdminConnectionString);

        async Task<string> SnapshotAsync()
        {
            await using var connection = new SqlConnection(journal.Application.AdminConnectionString);
            await connection.OpenAsync();
            await using var command = new SqlCommand("""
                SELECT (SELECT * FROM Accounting.SourceEvents ORDER BY Id FOR JSON PATH) SourceEvents,
                  (SELECT * FROM Accounting.JournalEntries ORDER BY Id FOR JSON PATH) JournalEntries,
                  (SELECT * FROM Accounting.JournalLines ORDER BY JournalId,Ordinal FOR JSON PATH) JournalLines,
                  (SELECT * FROM Accounting.PostingReceipts ORDER BY RequestId FOR JSON PATH) PostingReceipts
                FOR JSON PATH;
                """, connection);
            return await JournalControlTestContext.ReadCompleteJsonAsync(command);
        }
    }

    [Fact]
    public async Task FreshMigrationCreatesNoCalendarWithoutPostings()
    {
        // GIVEN a fresh disposable database.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        await using var admin = new SqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        // WHEN the current migration is inspected.
        await using var command = new SqlCommand("SELECT COUNT(*) FROM Accounting.Periods", admin);
        // THEN no arbitrary calendar horizon was generated.
        Assert.Equal(0, await command.ExecuteScalarAsync());
    }
}
