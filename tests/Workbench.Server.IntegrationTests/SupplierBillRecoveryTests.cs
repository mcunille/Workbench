// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SupplierBillRecoveryTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task GuardedRestoreAndSanitationPreserveBillHistory()
    {
        // GIVEN a posted source in this test's disposable database, with immutable review and retry history.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        var reviewed = await SupplierBillPostingTests.ReviewedAsync(context);
        await context.ExecuteAsync("PostSupplierBill", Guid.NewGuid(), context.PostCommand(reviewed));
        var before = await SnapshotAsync(context);
        var target = new SqlConnectionStringBuilder(context.Journal.Application.AdminConnectionString);
        var database = target.InitialCatalog;
        Assert.Matches("^workbench_test_[a-f0-9]+$", database);
        var path = $"/var/opt/mssql/data/{database}.bak";
        await using (var backup = new SqlConnection(target.ConnectionString))
        {
            await backup.OpenAsync();
            await using var command = new SqlCommand($"BACKUP DATABASE [{database}] TO DISK=@path WITH COPY_ONLY,CHECKSUM,INIT", backup);
            command.Parameters.AddWithValue("@path", path); await command.ExecuteNonQueryAsync();
        }
        await context.Journal.Connection.CloseAsync();
        // WHEN the owned database is restored, keep restricted access through independent pending-marker proof.
        var master = new SqlConnectionStringBuilder(target.ConnectionString) { InitialCatalog = "master", Pooling = false };
        await using (var restore = new SqlConnection(master.ConnectionString))
        {
            await restore.OpenAsync();
            await using var command = new SqlCommand($"""
                ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                RESTORE DATABASE [{database}] FROM DISK=@path WITH REPLACE,CHECKSUM,RECOVERY,RESTRICTED_USER;
                USE [{database}]; EXEC Administration.MarkRestorePending;
                """, restore) { CommandTimeout = 120 };
            command.Parameters.AddWithValue("@path", path); await command.ExecuteNonQueryAsync();
        }
        target.Pooling = false;
        await using (var proof = new SqlConnection(target.ConnectionString))
        {
            await proof.OpenAsync();
            await using var command = new SqlCommand("SELECT IsPending FROM Security.WorkbenchRestorePending", proof);
            Assert.True((bool)(await command.ExecuteScalarAsync())!);
            // THEN sanitation invalidates restored sessions without rewriting financial source evidence.
            command.CommandText = "EXEC Administration.SanitizeRestore @Now=@now,@CorrelationId=N'bk05-disposable-recovery'";
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow); await command.ExecuteNonQueryAsync();
            command.CommandText = "SELECT IsPending FROM Security.WorkbenchRestorePending";
            Assert.False((bool)(await command.ExecuteScalarAsync())!);
            command.CommandText = $"USE master; ALTER DATABASE [{database}] SET MULTI_USER";
            await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(before, await SnapshotAsync(context));
        Assert.Equal(0, await context.ScalarAsync<int>("SELECT COUNT(*) FROM [Identity].Sessions"));
    }

    private static Task<string> SnapshotAsync(SupplierBillTestContext context) => context.ScalarAsync<string>("""
        SELECT (SELECT * FROM Purchasing.SupplierBillRevisions ORDER BY Id FOR JSON PATH) Revisions,
          (SELECT * FROM Purchasing.SupplierBillReviews ORDER BY Id FOR JSON PATH) Reviews,
          (SELECT * FROM Purchasing.SupplierBillEvidence ORDER BY ReviewId,DocumentId FOR JSON PATH) Evidence,
          (SELECT * FROM Purchasing.SupplierBillPostings ORDER BY BillId FOR JSON PATH) Postings,
          (SELECT * FROM Purchasing.SupplierBillPostingEvents ORDER BY BillId,EventId FOR JSON PATH) Events,
          (SELECT * FROM Purchasing.SupplierBillReceipts ORDER BY Sequence FOR JSON PATH) Receipts
        FOR JSON PATH,WITHOUT_ARRAY_WRAPPER
        """);
}
