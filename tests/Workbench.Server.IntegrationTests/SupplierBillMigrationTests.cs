// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierBillMigrationTests(SqlServerFixture sqlServer)
{
    private const string PriorMigration = "20260926210900_AddPurchaseRecognition";

    [Fact]
    public async Task UpgradePreservesRecognitionHistoryAndExactReplay()
    {
        // GIVEN the merged BK-04 schema with real recognition, journal and retry evidence.
        await using var context = await PurchaseRecognitionTestContext.OpenAsync(sqlServer, PriorMigration);
        var source = await context.CommandAsync("Invoice"); var request = Guid.NewGuid();
        var posted = await context.PostAsync(source.ToJsonString(), request);
        var before = await SnapshotAsync(context.Journal.Connection);
        // WHEN the current release migrations apply from the BK-04 predecessor.
        await DatabaseMigrator.MigrateAsync(context.Journal.Application.AdminConnectionString, default);
        // THEN source bytes and the old command's result remain exact, including the kernel output adaptation.
        Assert.Equal(before, await SnapshotAsync(context.Journal.Connection));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(posted), System.Text.Json.JsonSerializer.Serialize(await context.PostAsync(source.ToJsonString(), request)));
        await MigrationHistoryAssertions.AssertCurrentAsync(context.Journal.Application.AdminConnectionString);
        Assert.Equal(50020, (await Assert.ThrowsAsync<SqlException>(() => DatabaseMigrator.MigrateToAsync(context.Journal.Application.AdminConnectionString, PriorMigration, default))).Number);
        Assert.Equal(before, await SnapshotAsync(context.Journal.Connection));
    }

    [Fact]
    public async Task FreshSchemaDeniesProductionMutationsAndContainsNoTestAdapters()
    {
        // GIVEN a genuinely empty disposable database.
        await using var database = await sqlServer.CreateDatabaseAsync();
        // WHEN the current consolidated migration is installed.
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        await MigrationHistoryAssertions.AssertCurrentAsync(database.AdminConnectionString);
        await using var admin = new SqlConnection(database.AdminConnectionString); await admin.OpenAsync();
        await using var command = new SqlCommand("SELECT COUNT(*) FROM sys.procedures WHERE name LIKE '%Fixture%' OR name LIKE '%Synthetic%' OR name LIKE '%ForTest'", admin);
        // THEN disposable source adapters and production role assignments are absent.
        Assert.Equal(0, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM [Identity].RoleClaims WHERE ClaimValue IN(N'SupplierBillsManage',N'SupplierBillsPost',N'SupplierPaymentsRecord',N'SupplierPaymentsCorrect',N'SupplierAllocationsManage')";
        Assert.Equal(0, await command.ExecuteScalarAsync());
        command.CommandText = "CREATE USER bill_permission_probe WITHOUT LOGIN; ALTER ROLE workbench_web ADD MEMBER bill_permission_probe; EXECUTE AS USER='bill_permission_probe';";
        await command.ExecuteNonQueryAsync();
        try
        {
            foreach (var sql in new[]
            {
                "EXEC Purchasing.SaveSupplierBill", "EXEC Purchasing.ReviewSupplierBill", "EXEC Purchasing.PostSupplierBill",
                "UPDATE Purchasing.SupplierBills SET State='Posted' WHERE 1=0", "DELETE Purchasing.SupplierBillRevisions WHERE 1=0",
                "DELETE Purchasing.SupplierBillReviews WHERE 1=0", "DELETE Purchasing.SupplierBillEvidence WHERE 1=0",
                "DELETE Purchasing.SupplierBillPostings WHERE 1=0", "DELETE Purchasing.SupplierBillPostingEvents WHERE 1=0",
                "DELETE Purchasing.SupplierBillReceipts WHERE 1=0"
            })
            {
                command.CommandText = sql;
                Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
            }
        }
        finally { command.CommandText = "REVERT"; await command.ExecuteNonQueryAsync(); }
    }

    private static async Task<string> SnapshotAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand("""
            SELECT (SELECT * FROM Purchasing.RecognitionSideEvents ORDER BY Id FOR JSON PATH) Events,
              (SELECT * FROM Purchasing.RecognitionGroupReceipts ORDER BY RequestId FOR JSON PATH) Receipts,
              (SELECT * FROM Accounting.JournalEntries ORDER BY Id FOR JSON PATH) Journals,
              (SELECT * FROM Accounting.JournalLines ORDER BY JournalId,Ordinal FOR JSON PATH) Lines,
              (SELECT * FROM Accounting.Configurations ORDER BY TenantId FOR JSON PATH) Configurations
            FOR JSON PATH;
            """, connection);
        return await JournalControlTestContext.ReadCompleteJsonAsync(command);
    }
}
