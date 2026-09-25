// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using System.Text.Json;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class JournalPolicyTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task DescriptiveChangesRemainAllowedButNetZeroHistoricalAccountCannotBeArchived()
    {
        // GIVEN two opposite exact postings whose activity leaves both accounts at net zero.
        await using var context = await JournalTestContext.OpenAsync(sqlServer);
        await context.CreateGeneralAccountsAsync();
        await context.ConfigureAsync();
        var first = await context.CreateSourceAsync();
        await context.PostAsync(first);
        var second = await context.CreateSourceAsync();
        await using (var admin = new SqlConnection(context.Application.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var swap = new SqlCommand("""
                UPDATE Accounting.SyntheticSources
                   SET DebitAccountId=@credit,CreditAccountId=@debit
                 WHERE TenantId=@tenant AND Id=@source;
                """, admin);
            swap.Parameters.AddWithValue("@credit", context.CreditAccountId);
            swap.Parameters.AddWithValue("@debit", context.DebitAccountId);
            swap.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
            swap.Parameters.AddWithValue("@source", second.Id);
            Assert.Equal(1, await swap.ExecuteNonQueryAsync());
        }
        await context.PostAsync(second);
        // WHEN descriptive configuration notes and posted account labels are edited.
        var notes = JsonSerializer.Serialize(new
        {
            policies = new
            {
                country = "US",
                region = "CA",
                currency = "USD",
                scale = 2,
                fiscalStartMonth = 1,
                startApproach = "OpeningBalances",
                plannedStartDate = "2026-01-01",
                frameworkNotes = "Updated review note"
            },
            mappings = Array.Empty<object>(),
            coverage = Array.Empty<object>()
        });
        var configuration = await context.SaveAsync(Guid.NewGuid(), "Configure", notes,
            expectedVersion: context.ConfigurationVersion);
        var account = await context.SaveAsync(Guid.NewGuid(), "UpdateAccount",
            """{"code":"1910","name":"Renamed synthetic debit","description":"Historical label retained on lines"}""",
            context.DebitAccountId, context.DebitAccountVersion);
        // THEN descriptive changes append revisions, while archived history still cannot be removed.
        Assert.NotEqual(context.ConfigurationVersion, configuration.Version);
        Assert.NotEqual(context.DebitAccountVersion, account.Version);
        foreach (var id in new[] { context.DebitAccountId, context.CreditAccountId })
        {
            await using var balance = new SqlCommand("""
                SELECT SUM(CONVERT(decimal(38,4),Debit)-CONVERT(decimal(38,4),Credit))
                  FROM Accounting.JournalLines WHERE TenantId=@tenant AND AccountId=@account;
                """, context.Connection);
            balance.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
            balance.Parameters.AddWithValue("@account", id);
            Assert.Equal(0m, (decimal)(await balance.ExecuteScalarAsync())!);
        }
        await using (var historicalName = new SqlCommand("""
            SELECT COUNT(*) FROM Accounting.JournalLines
             WHERE TenantId=@tenant AND AccountId=@account AND AccountName=N'Synthetic debit';
            """, context.Connection))
        {
            historicalName.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
            historicalName.Parameters.AddWithValue("@account", context.DebitAccountId);
            Assert.Equal(2, (int)(await historicalName.ExecuteScalarAsync())!);
        }
        var archive = await Assert.ThrowsAsync<SqlException>(() => context.SaveAsync(Guid.NewGuid(), "ArchiveAccount",
            """{"isArchived":true}""", context.DebitAccountId, account.Version));
        Assert.Equal(50909, archive.Number);
        var unused = await context.SaveAsync(Guid.NewGuid(), "CreateAccounts",
            """[{"code":"3910","name":"Unused General","type":"Equity","purpose":"General"}]""");
        var unusedId = JsonSerializer.Deserialize<Guid[]>(unused.Ids)![0];
        await context.SaveAsync(Guid.NewGuid(), "ArchiveAccount", """{"isArchived":true}""", unusedId, unused.Version);
        await using (var unusedState = new SqlCommand("SELECT ArchivedAtUtc FROM Accounting.Accounts WHERE TenantId=@tenant AND Id=@id", context.Connection))
        {
            unusedState.Parameters.AddWithValue("@tenant", JournalTestContext.TenantId);
            unusedState.Parameters.AddWithValue("@id", unusedId);
            Assert.IsType<DateTimeOffset>(await unusedState.ExecuteScalarAsync());
        }
        Assert.Equal(2, await context.CountAsync("JournalEntries"));
    }
}
