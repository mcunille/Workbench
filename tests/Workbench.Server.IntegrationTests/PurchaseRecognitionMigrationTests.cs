// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class PurchaseRecognitionMigrationTests(SqlServerFixture sqlServer)
{
    private const string PriorMigration = "20260925044758_AddAccountingPeriodControls";

    [Fact]
    public async Task UpgradePreservesOriginalCorrectionClosureAndReceiptBytesAndReplay()
    {
        // GIVEN the merged BK-03 schema with original, replacement and closed-period evidence.
        await using var context = await JournalControlTestContext.OpenAsync(sqlServer, priorMigration: PriorMigration);
        var journal = context.Journal;
        var source = await journal.CreateSourceAsync();
        var request = Guid.NewGuid();
        var original = await journal.PostAsync(source, request);
        var correctionRequest = Guid.NewGuid();
        var corrected = await context.CorrectAsync(original.JournalId, new DateOnly(2026, 2, 2), "10.25", correctionRequest);
        var closeRequest = Guid.NewGuid();
        var closed = await context.CloseAsync(new DateOnly(2026, 2, 1), closeRequest);
        var history = await context.HistorySnapshotAsync();
        var configuration = await ConfigurationSnapshotAsync();
        using (var retained = JsonDocument.Parse(history))
        {
            Assert.Equal(3, retained.RootElement[0].GetProperty("JournalEntries").GetArrayLength());
            Assert.Single(retained.RootElement[0].GetProperty("CorrectionGroups").EnumerateArray());
            Assert.Single(retained.RootElement[0].GetProperty("PeriodClosures").EnumerateArray());
        }
        using var client = journal.Application.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);

        // WHEN the single BK-04 release upgrades its merged predecessor.
        await DatabaseMigrator.MigrateAsync(journal.Application.AdminConnectionString, default);

        // THEN every retained ID, amount, snapshot and receipt byte survives, including exact retries.
        Assert.Equal(history, await context.HistorySnapshotAsync());
        Assert.Equal(configuration, await ConfigurationSnapshotAsync());
        Assert.Equal(original, await journal.PostAsync(source, request));
        Assert.Equal(corrected, await context.CorrectAsync(original.JournalId, new DateOnly(2026, 2, 2), "10.25", correctionRequest));
        Assert.Equal(closed, await context.CloseAsync(new DateOnly(2026, 2, 1), closeRequest));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        await MigrationHistoryAssertions.AssertCurrentAsync(journal.Application.AdminConnectionString);
        await using var admin = new SqlConnection(journal.Application.AdminConnectionString);
        await admin.OpenAsync();
        await using var count = new SqlCommand("SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId>@prior", admin);
        count.Parameters.AddWithValue("@prior", PriorMigration);
        Assert.Equal(CurrentSchema.Migrations.Count(m => string.CompareOrdinal(m, PriorMigration) > 0), await count.ExecuteScalarAsync());
        // AND a destructive rollback cannot erase the preserved history.
        var error = await Assert.ThrowsAsync<SqlException>(() => DatabaseMigrator.MigrateToAsync(
            journal.Application.AdminConnectionString, PriorMigration, default));
        Assert.Equal(50020, error.Number);
        Assert.Equal(history, await context.HistorySnapshotAsync());
        await MigrationHistoryAssertions.AssertCurrentAsync(journal.Application.AdminConnectionString);

        async Task<string> ConfigurationSnapshotAsync()
        {
            await using var query = new SqlCommand("""
                SELECT (SELECT * FROM Accounting.Accounts ORDER BY Id FOR JSON PATH) Accounts,
                  (SELECT * FROM Accounting.Configurations ORDER BY TenantId FOR JSON PATH) Configurations,
                  (SELECT * FROM Accounting.Revisions ORDER BY Id FOR JSON PATH) Revisions,
                  (SELECT * FROM Accounting.Receipts ORDER BY RequestId FOR JSON PATH) Receipts,
                  (SELECT * FROM Accounting.PolicyFreezes ORDER BY TenantId FOR JSON PATH) PolicyFreezes
                FOR JSON PATH;
                """, journal.Connection);
            return await JournalControlTestContext.ReadCompleteJsonAsync(query);
        }
    }

    [Fact]
    public async Task FreshReleaseInstallsRestrictedCommandsWithoutSyntheticAdaptersAndRejectsOldBinary()
    {
        // GIVEN a genuinely empty disposable database, without the test adapter template.
        await using var database = await sqlServer.CreateDatabaseAsync();
        // WHEN all migrations are applied from current source.
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        await MigrationHistoryAssertions.AssertCurrentAsync(database.AdminConnectionString);
        await using var admin = new SqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        // THEN only the matching release marker is accepted.
        foreach (var expected in new[] { PriorMigration, CurrentSchema.MigrationId })
        {
            await using var readiness = new SqlCommand("EXEC Security.ReadDatabaseReadiness @ExpectedMigration=@expected", admin);
            readiness.Parameters.AddWithValue("@expected", expected);
            await using var reader = await readiness.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(expected == CurrentSchema.MigrationId, reader.GetBoolean(reader.GetOrdinal("CompatibleMigration")));
        }
        await using var command = new SqlCommand("""
            SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped=0
              AND (name LIKE '%Synthetic%' OR name LIKE '%FixtureRecognition%');
            """, admin);
        Assert.Equal(0, await command.ExecuteScalarAsync());
        foreach (var procedure in new[] { "PostRecognition", "CorrectRecognition" })
        {
            command.CommandText = $"SELECT COUNT(*) FROM sys.procedures WHERE object_id=OBJECT_ID(N'Purchasing.{procedure}')";
            Assert.Equal(1, await command.ExecuteScalarAsync());
        }
        command.CommandText = "CREATE USER recognition_migration_probe WITHOUT LOGIN; ALTER ROLE workbench_web ADD MEMBER recognition_migration_probe; EXECUTE AS USER='recognition_migration_probe';";
        await command.ExecuteNonQueryAsync();
        try
        {
            // AND runtime role membership grants neither direct command execution nor financial writes.
            foreach (var sql in new[]
            {
                "EXEC Purchasing.PostRecognition", "EXEC Purchasing.CorrectRecognition",
                "UPDATE Purchasing.RecognitionUnits SET Classification='Expense' WHERE 1=0",
                "DELETE FROM Purchasing.RecognitionGroupReceipts WHERE 1=0",
                "UPDATE Accounting.JournalEntries SET Reference=N'changed' WHERE 1=0",
            })
            {
                command.CommandText = sql;
                Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
            }
        }
        finally
        {
            command.CommandText = "REVERT";
            await command.ExecuteNonQueryAsync();
        }
    }
}
