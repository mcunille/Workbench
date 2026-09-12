// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SharedAcquisitionDatabaseTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData("AddAcquisitionContext")]
    [InlineData("AddSharedAcquisitions")]
    [InlineData("AddAcquisitionDocuments")]
    public async Task UpgradeFromAcquisitionContextPreservesRelationshipsAndBlocksDestructiveDown(string priorMigration)
    {
        // GIVEN saved shared context and immutable creation evidence on the PR base schema.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateToAsync(database.AdminConnectionString, priorMigration, default);
        var tenant = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid());
        await using var admin = new SqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        await SeedAsync(admin, tenant);
        var before = await Snapshot(admin);
        // WHEN applying the current migration THEN every saved row and token is retained exactly.
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        Assert.Equal(before, await Snapshot(admin));
        await using var marker = new SqlCommand("SELECT OBJECT_DEFINITION(OBJECT_ID(N'Security.ReadDatabaseReadiness'))", admin);
        Assert.Contains("20260912045432_AddDraftOrderDeletion", (string)(await marker.ExecuteScalarAsync())!);
        // AND rollback cannot discard relationship corrections or shared context.
        var migrator = await database.CreateRoleUserAsync("workbench_migrator");
        Assert.Equal(50020, (await Assert.ThrowsAsync<SqlException>(() => DatabaseMigrator.MigrateToAsync(migrator, "AddAcquisitionContext", default))).Number);
        Assert.Equal(before, await Snapshot(admin));
    }

    [Fact]
    public async Task RestrictedSqlLinkCommandEnforcesTenantVersionsAndAtomicRollback()
    {
        // GIVEN owner and foreign rows and a restricted runtime SQL session under owner tenant proof.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid();
        var foreignTenant = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, foreignTenant);
        await using var admin = new SqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        var owner = await SeedAsync(admin, tenant);
        var other = await SeedAsync(admin, foreignTenant);
        var web = await database.CreateWebUserAsync();
        await using var sql = new SqlConnection(web);
        await sql.OpenAsync();
        await new TenantContextProof(await database.GetTenantContextProofKeyAsync()).ApplyAsync(sql, tenant, default);
        // WHEN directly invoking with foreign item, old context or target context THEN SQL returns 404 status without changes.
        Assert.Equal(0, await Change(sql, other.Item, other.ItemVersion, other.Acquisition, other.Version, null, null));
        Assert.Equal(0, await Change(sql, owner.Item, owner.ItemVersion, other.Acquisition, other.Version, null, null));
        Assert.Equal(0, await Change(sql, owner.Item, owner.ItemVersion, owner.Acquisition, owner.Version, other.Acquisition, other.Version));
        foreach (var token in new[] { Array.Empty<byte>(), new byte[7], new byte[9] })
            Assert.Equal(50045, (await Assert.ThrowsAsync<SqlException>(() => Change(sql, owner.Item, token, owner.Acquisition, owner.Version, null, null))).Number);
        Assert.Equal(50045, (await Assert.ThrowsAsync<SqlException>(() => Change(sql, owner.Item, owner.ItemVersion, null, owner.Version, null, null))).Number);
        Assert.Equal(50045, (await Assert.ThrowsAsync<SqlException>(() => Change(sql, owner.Item, owner.ItemVersion, owner.Acquisition, null, null, null))).Number);
        var before = await Snapshot(admin);
        // WHEN a trigger fails after link deletion THEN XACT_ABORT prevents a caller committing any partial mutation.
        await using (var inject = new SqlCommand("CREATE TRIGGER Inventory.InjectedLinkFailure ON Inventory.Acquisitions AFTER UPDATE AS THROW 50049, 'Injected failure', 1;", admin))
            await inject.ExecuteNonQueryAsync();
        Assert.Equal(50049, (await Assert.ThrowsAsync<SqlException>(() => Change(sql, owner.Item, owner.ItemVersion, owner.Acquisition, owner.Version, null, null))).Number);
        Assert.Equal(before, await Snapshot(admin));
        await using (var recover = new SqlCommand("DROP TRIGGER Inventory.InjectedLinkFailure", admin)) await recover.ExecuteNonQueryAsync();
        // THEN a valid owner removal advances both versions, retains immutable evidence and does not affect foreign rows.
        Assert.Equal(1, await Change(sql, owner.Item, owner.ItemVersion, owner.Acquisition, owner.Version, null, null));
        Assert.Equal(2, await Change(sql, owner.Item, owner.ItemVersion, owner.Acquisition, owner.Version, null, null));
        await using var verify = new SqlCommand("SELECT COUNT(*) FROM Inventory.AcquisitionCreationRecords; SELECT COUNT(*) FROM Inventory.AcquisitionItems;", admin);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OppositeReplacementWaitsOnHeldSqlLocksAndRejectsStaleMembership(bool creationReplay)
    {
        // GIVEN two restricted SQL sessions with two original linked pieces in one tenant.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid());
        await using var admin = new SqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        var a = await SeedAsync(admin, tenant);
        var b = await SeedAsync(admin, tenant);
        var web = await database.CreateWebUserAsync();
        await using var first = new SqlConnection(web);
        await using var second = new SqlConnection(web);
        await first.OpenAsync();
        await second.OpenAsync();
        var proof = new TenantContextProof(await database.GetTenantContextProofKeyAsync());
        await proof.ApplyAsync(first, tenant, default);
        await proof.ApplyAsync(second, tenant, default);
        await using var transaction = (SqlTransaction)await first.BeginTransactionAsync();
        await using var winner = Command(first, transaction, a.Item, a.ItemVersion, a.Acquisition, a.Version, b.Acquisition, b.Version);
        Assert.Equal(1, (int)(await winner.ExecuteScalarAsync())!);
        // WHEN an opposite replacement or old creation replay starts while correction holds SQL locks.
        var waiting = creationReplay ? Replay(second, a.Item, a.ItemVersion) : Change(second, b.Item, b.ItemVersion, b.Acquisition, b.Version, a.Acquisition, a.Version);
        // Inspect SQL Server's actual blocking relationship, rather than relying on HTTP scheduling.
        var blocked = false;
        for (var attempt = 0; attempt < 100 && !blocked; attempt++)
        {
            await using var blocking = new SqlCommand("SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id=@blocker AND database_id=DB_ID()", admin);
            blocking.Parameters.AddWithValue("@blocker", first.ServerProcessId);
            blocked = (int)(await blocking.ExecuteScalarAsync())! > 0;
            if (!blocked) await Task.Delay(20);
        }
        Assert.True(blocked, "The competing SQL request must demonstrably wait for the first transaction's locks.");
        Assert.False(waiting.IsCompleted);
        // THEN commit releases the waiter; stale replacement conflicts and creation replay preserves the corrected links.
        await transaction.CommitAsync();
        Assert.Equal(creationReplay ? 4 : 2, await waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        await using var links = new SqlCommand("SELECT COUNT(*) FROM Inventory.AcquisitionItems WHERE AcquisitionId=@target", admin);
        links.Parameters.AddWithValue("@target", b.Acquisition);
        Assert.Equal(2, await links.ExecuteScalarAsync());
    }
    private static async Task<int> Replay(SqlConnection sql, Guid item, byte[] version)
    {
        await using var transaction = (SqlTransaction)await sql.BeginTransactionAsync();
        await using var command = new SqlCommand("""
            DECLARE @request uniqueidentifier;
            SELECT @request=CreationRequestId FROM Inventory.AcquisitionCreationRecords WHERE ItemId=@item;
            EXEC Inventory.CreateAcquisition @ItemId=@item,@CreationRequestId=@request,@ExpectedItemVersion=@version,@Method=N'Gift',@Source=N'Preserved origin';
            """, sql, transaction);
        command.Parameters.AddWithValue("@item", item);
        command.Parameters.AddWithValue("@version", version);
        var result = (int)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return result;
    }

    private static async Task<int> Change(SqlConnection sql, Guid item, byte[] version, Guid? old, byte[]? oldVersion, Guid? target, byte[]? targetVersion)
    {
        await using var transaction = (SqlTransaction)await sql.BeginTransactionAsync();
        await using var command = Command(sql, transaction, item, version, old, oldVersion, target, targetVersion);
        var result = (int)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return result;
    }

    private static SqlCommand Command(SqlConnection sql, SqlTransaction transaction, Guid item, byte[] version, Guid? old, byte[]? oldVersion, Guid? target, byte[]? targetVersion)
    {
        var command = new SqlCommand("Inventory.ChangeAcquisitionLink", sql, transaction) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@ItemId", item);
        command.Parameters.Add(new SqlParameter("@ExpectedItemVersion", SqlDbType.VarBinary, -1) { Value = version });
        command.Parameters.Add(new SqlParameter("@ExpectedAcquisitionId", SqlDbType.UniqueIdentifier) { Value = (object?)old ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@ExpectedAcquisitionVersion", SqlDbType.VarBinary, -1) { Value = (object?)oldVersion ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@TargetAcquisitionId", SqlDbType.UniqueIdentifier) { Value = (object?)target ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@TargetAcquisitionVersion", SqlDbType.VarBinary, -1) { Value = (object?)targetVersion ?? DBNull.Value });
        return command;
    }

    private static async Task<(Guid Item, byte[] ItemVersion, Guid Acquisition, byte[] Version)> SeedAsync(SqlConnection sql, Guid tenant)
    {
        await using var seed = new SqlCommand("""
            DECLARE @item uniqueidentifier=NEWID(),@acquisition uniqueidentifier=NEWID(),@request uniqueidentifier=NEWID();
            INSERT Inventory.Items (Id,TenantId,TrackingKind,Name,CreatedAtUtc,CreationRequestId)
                VALUES (@item,@tenant,'Individual',N'Preserved piece',SYSUTCDATETIME(),NEWID());
            INSERT Inventory.Acquisitions (Id,TenantId,Method,Source,CreatedAtUtc,CreationRequestId)
                VALUES (@acquisition,@tenant,'Gift',N'Preserved origin',SYSUTCDATETIME(),@request);
            INSERT Inventory.AcquisitionItems (TenantId,ItemId,AcquisitionId) VALUES (@tenant,@item,@acquisition);
            INSERT Inventory.AcquisitionCreationRecords (TenantId,CreationRequestId,AcquisitionId,ItemId,Method,Source)
                VALUES (@tenant,@request,@acquisition,@item,'Gift',N'Preserved origin');
            SELECT i.Id,i.RowVersion,a.Id,a.RowVersion FROM Inventory.Items i CROSS JOIN Inventory.Acquisitions a WHERE i.Id=@item AND a.Id=@acquisition;
            """, sql);
        seed.Parameters.AddWithValue("@tenant", tenant);
        await using var reader = await seed.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetGuid(0), (byte[])reader[1], reader.GetGuid(2), (byte[])reader[3]);
    }

    private static async Task<string> Snapshot(SqlConnection sql)
    {
        await using var command = new SqlCommand("""
            SELECT (SELECT * FROM Inventory.Items ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES)+
                (SELECT * FROM Inventory.Acquisitions ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES)+
                (SELECT * FROM Inventory.AcquisitionItems ORDER BY ItemId FOR JSON PATH,INCLUDE_NULL_VALUES)+
                (SELECT * FROM Inventory.AcquisitionCreationRecords ORDER BY ItemId FOR JSON PATH,INCLUDE_NULL_VALUES);
            """, sql);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
