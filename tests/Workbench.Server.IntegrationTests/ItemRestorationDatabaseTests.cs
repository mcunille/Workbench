// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ItemRestorationDatabaseTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData("restore")]
    [InlineData("text")]
    [InlineData("photo")]
    public async Task IndependentConnectionsCannotReuseArchivedVersion(string competitor)
    {
        // GIVEN an archived item and independent restricted connections sharing its version.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var tenant = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid());
        await using var admin = new SqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        await using var first = await ConnectAsync(database, tenant);
        await using var second = await ConnectAsync(database, tenant);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var id = Guid.NewGuid();
            var version = await SeedAsync(admin, tenant, id);
            Assert.Equal(1, await ArchiveAsync(first, id, version));
            var archived = await RecordAsync(admin, id);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<bool> RestoreRaceAsync() { await start.Task; return await RestoreAsync(first, id, archived.Version) == 1; }
            async Task<bool> CompetitorAsync()
            {
                await start.Task;
                return competitor switch
                {
                    "restore" => await RestoreAsync(second, id, archived.Version) == 1,
                    "text" => await EditAsync(second, id, archived.Version) == 1,
                    _ => await PhotoAsync(second, id, archived.Version),
                };
            }
            // WHEN both commands compete for the same archived version.
            var restore = RestoreRaceAsync();
            var other = CompetitorAsync();
            start.SetResult();
            var results = await Task.WhenAll(restore, other);
            // THEN only one restoration can commit and stale text/photo commands cannot mutate.
            Assert.Single(results, succeeded => succeeded);
            if (competitor != "restore") Assert.True(results[0]);
            var saved = await RecordAsync(admin, id);
            Assert.Null(saved.ArchivedAtUtc);
            Assert.Equal(archived.Fields, saved.Fields);
            Assert.NotEqual(archived.Version, saved.Version);
            Assert.Equal(3, await RestoreAsync(first, id, archived.Version));
            Assert.Equal(1, await ArchiveAsync(first, id, saved.Version));
            var rearchived = await RecordAsync(admin, id);
            Assert.Equal(2, await RestoreAsync(first, id, archived.Version));
            Assert.Equal(rearchived.Version, (await RecordAsync(admin, id)).Version);
        }
    }

    [Fact]
    public async Task RestoreEnforcesTenantTransactionVersionAndRollback()
    {
        // GIVEN a fresh database with an archived record under restricted caller RLS.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, otherTenant);
        await using var admin = new SqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        await using var owner = await ConnectAsync(database, tenant);
        await using var foreign = await ConnectAsync(database, otherTenant);
        var id = Guid.NewGuid();
        await ArchiveAsync(owner, id, await SeedAsync(admin, tenant, id));
        var archived = await RecordAsync(admin, id);
        // WHEN SQL callers omit the transaction or send malformed tokens.
        await using (var command = new SqlCommand("EXEC [Inventory].[RestoreItem] @Id=@id,@ExpectedVersion=@version", owner))
        {
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@version", archived.Version);
            Assert.Equal(50044, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync())).Number);
        }
        foreach (var version in new byte[][] { [], [1], new byte[9] })
            Assert.Equal(50044, (await Assert.ThrowsAsync<SqlException>(() => RestoreAsync(owner, id, version))).Number);
        await using (var transaction = (SqlTransaction)await owner.BeginTransactionAsync())
        {
            await using var command = new SqlCommand("EXEC [Inventory].[RestoreItem] @Id=@id,@ExpectedVersion=NULL", owner, transaction);
            command.Parameters.AddWithValue("@id", id);
            Assert.Equal(50044, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync())).Number);
        }
        // THEN invalid and other-tenant requests leave the archived row unchanged.
        Assert.Equal(0, await RestoreAsync(foreign, id, archived.Version));
        Assert.Equal(0, await RestoreAsync(owner, Guid.NewGuid(), archived.Version));
        Assert.Equal(2, await RestoreAsync(owner, id, new byte[8]));
        foreach (var statement in new[] { "UPDATE [Inventory].[Items] SET [ArchivedAtUtc]=NULL", "DELETE FROM [Inventory].[Items]" })
        {
            await using var denied = new SqlCommand(statement, owner);
            Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => denied.ExecuteNonQueryAsync())).Number);
        }
        // WHEN restoration executes but its enclosing transaction is rolled back.
        await using (var transaction = (SqlTransaction)await owner.BeginTransactionAsync())
        {
            await using var command = new SqlCommand("EXEC [Inventory].[RestoreItem] @Id=@id,@ExpectedVersion=@version", owner, transaction);
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@version", archived.Version);
            Assert.Equal(1, await command.ExecuteScalarAsync());
            await transaction.RollbackAsync();
        }
        // THEN no uncommitted membership or version change escapes and the original token still works.
        var rolledBack = await RecordAsync(admin, id);
        Assert.Equal(archived.ArchivedAtUtc, rolledBack.ArchivedAtUtc);
        Assert.Equal(archived.Version, rolledBack.Version);
        Assert.Equal(1, await RestoreAsync(owner, id, archived.Version));
    }
    [Fact]
    public async Task UpgradeAndDownRetainArchivedEditedIdentityAndPhotoHistory()
    {
        // GIVEN SQL-seeded data on the actual PR base schema, without using a newer EF model.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateToAsync(database.AdminConnectionString, "AddOnlineRecovery", CancellationToken.None);
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, otherTenant);
        await using var admin = new SqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        var id = Guid.NewGuid();
        await SeedAsync(admin, tenant, id);
        await using (var seed = new SqlCommand("""
            DECLARE @detail uniqueidentifier=NEWID(), @thumb uniqueidentifier=NEWID(), @operation uniqueidentifier=NEWID(),
                @photo uniqueidentifier=NEWID(), @detailRevision uniqueidentifier=NEWID(), @thumbRevision uniqueidentifier=NEWID();
            INSERT [Inventory].[ItemCreationSnapshots] ([TenantId],[ItemId],[Name],[Notes],[StorageLocation])
                SELECT [TenantId],[Id],[Name],[Notes],[StorageLocation] FROM [Inventory].[Items] WHERE [Id]=@id;
            INSERT [Storage].[Attachments] ([Id],[TenantId],[CreatedAtUtc])
                VALUES (@detail,@tenant,SYSUTCDATETIME()),(@thumb,@tenant,SYSUTCDATETIME());
            INSERT [Storage].[Revisions]
                ([Id],[TenantId],[AttachmentId],[OperationId],[ActorUserId],[ProviderAlias],[Source],[MediaType],[Length],[Sha256],[State],[CreatedAtUtc])
                VALUES (@detailRevision,@tenant,@detail,NEWID(),NEWID(),N'local',N'ItemPhoto',N'image/webp',12,REPLICATE('A',64),1,SYSUTCDATETIME()),
                    (@thumbRevision,@tenant,@thumb,NEWID(),NEWID(),N'local',N'ItemPhoto',N'image/webp',6,REPLICATE('B',64),1,SYSUTCDATETIME());
            DECLARE @report uniqueidentifier=NEWID();
            INSERT [Security].[FileRecoveryReports] VALUES (@report,1,REPLICATE('C',64),N'local',SYSUTCDATETIME());
            INSERT [Storage].[RecoveryFiles] VALUES (@tenant,@detailRevision,@report,1,'Missing',SYSUTCDATETIME());
            UPDATE [Storage].[Attachments] SET [CurrentRevisionId]=@detailRevision WHERE [Id]=@detail;
            UPDATE [Storage].[Attachments] SET [CurrentRevisionId]=@thumbRevision WHERE [Id]=@thumb;
            DECLARE @version varbinary(8)=(SELECT [RowVersion] FROM [Inventory].[Items] WHERE [Id]=@id);
            INSERT [Inventory].[ItemPhotoOperations]
                ([Id],[TenantId],[ItemId],[RequestId],[ExpectedVersion],[PayloadSha256],[Kind],[State],[ActorUserId],[CreatedAtUtc],
                    [DetailAttachmentId],[ThumbnailAttachmentId],[ResultVersion])
                VALUES (@operation,@tenant,@id,NEWID(),@version,REPLICATE('A',64),0,1,NEWID(),SYSUTCDATETIME(),@detail,@thumb,@version),
                    (NEWID(),@tenant,@id,NEWID(),@version,REPLICATE('B',64),1,0,NEWID(),SYSUTCDATETIME(),NULL,NULL,NULL);
            INSERT [Inventory].[ItemPhotos]
                ([Id],[TenantId],[ItemId],[OperationId],[DetailAttachmentId],[ThumbnailAttachmentId],[Width],[Height],[CreatedAtUtc])
                VALUES (@photo,@tenant,@id,@operation,@detail,@thumb,640,480,SYSUTCDATETIME());
            UPDATE [Inventory].[Items] SET [Name]=N'Edited before upgrade',[CurrentPhotoId]=@photo WHERE [Id]=@id;
            """, admin))
        {
            seed.Parameters.AddWithValue("@id", id);
            seed.Parameters.AddWithValue("@tenant", tenant);
            await seed.ExecuteNonQueryAsync();
        }
        await using var owner = await ConnectAsync(database, tenant);
        var before = await RecordAsync(admin, id);
        Assert.Equal(1, await ArchiveAsync(owner, id, before.Version));
        var archived = await RecordAsync(admin, id);
        var retained = await RetainedEvidenceAsync(admin);
        var recoverySchema = await RecoverySchemaAsync(admin);
        // WHEN the additive restoration migration upgrades the online-recovery base data.
        await DatabaseMigrator.MigrateToAsync(database.AdminConnectionString, "AddItemRestoration", CancellationToken.None);
        // THEN current readiness advances while recovery commands, grants and tenant isolation survive.
        await AssertMigrationMarkerAsync(admin, "20260908010000_AddItemRestoration");
        Assert.Equal(recoverySchema, await RecoverySchemaAsync(admin));
        // AND all retained data and archive membership survive unchanged.
        Assert.Equal(retained, await RetainedEvidenceAsync(admin));
        Assert.Equal(archived.Version, (await RecordAsync(admin, id)).Version);
        Assert.Equal(1, await RestoreAsync(owner, id, archived.Version));
        var restored = await RecordAsync(admin, id);
        Assert.Equal(archived.Fields, restored.Fields);
        Assert.Null(restored.ArchivedAtUtc);
        Assert.Equal(retained, await RetainedEvidenceAsync(admin));
        // WHEN the supported down migration removes only the additive command.
        await DatabaseMigrator.MigrateToAsync(database.AdminConnectionString, "AddOnlineRecovery", CancellationToken.None);
        // THEN it preserves recovery commands, restoration, versions and history and restores the prior schema marker.
        Assert.Equal(recoverySchema, await RecoverySchemaAsync(admin));
        await AssertMigrationMarkerAsync(admin, "20260907225320_AddOnlineRecovery");
        Assert.Equal(restored.Version, (await RecordAsync(admin, id)).Version);
        Assert.Null((await RecordAsync(admin, id)).ArchivedAtUtc);
        Assert.Equal(retained, await RetainedEvidenceAsync(admin));
        await using var metadata = new SqlCommand("SELECT OBJECT_ID(N'[Inventory].[RestoreItem]'), OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'))", admin);
        await using (var reader = await metadata.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.IsDBNull(0));
            Assert.Contains("20260907225320_AddOnlineRecovery", reader.GetString(1));
            Assert.DoesNotContain("AddItemRestoration", reader.GetString(1));
        }
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        Assert.Equal(3, await RestoreAsync(owner, id, archived.Version));
    }
    private static async Task AssertMigrationMarkerAsync(SqlConnection connection, string migration)
    {
        await using var command = new SqlCommand("SELECT OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'))", connection);
        Assert.Contains(migration, (string)(await command.ExecuteScalarAsync())!);
    }

    private static async Task<string> RecoverySchemaAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand("""
            SELECT (SELECT name, OBJECT_DEFINITION(object_id) AS definition FROM sys.procedures
                WHERE name IN ('ReadRecoveryInventory','AcceptFileRecovery','ReadFileRecoveryCompletion','ReadFileRecoveryReadiness')
                ORDER BY name FOR JSON PATH) AS commands,
                (SELECT principal.name, permission.permission_name, permission.state_desc, OBJECT_NAME(permission.major_id) AS object_name
                FROM sys.database_permissions permission JOIN sys.database_principals principal ON principal.principal_id=permission.grantee_principal_id
                WHERE permission.major_id IN (OBJECT_ID('Storage.RecoveryFiles'),OBJECT_ID('Storage.ReadRecoveryInventory'),
                    OBJECT_ID('Storage.AcceptFileRecovery'),OBJECT_ID('Storage.ReadFileRecoveryCompletion'),OBJECT_ID('Security.ReadFileRecoveryReadiness'))
                ORDER BY principal.name, object_name, permission.permission_name FOR JSON PATH) AS grants,
                (SELECT predicate_definition, predicate_type_desc, operation_desc FROM sys.security_predicates
                WHERE target_object_id=OBJECT_ID('Storage.RecoveryFiles') ORDER BY predicate_type, operation FOR JSON PATH) AS predicates
            FOR JSON PATH;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var json = new System.Text.StringBuilder();
        while (await reader.ReadAsync())
        {
            json.Append(reader.GetString(0));
        }
        return json.ToString();
    }

    private static async Task<string> RetainedEvidenceAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand("""
            SELECT (SELECT * FROM [Inventory].[ItemCreationSnapshots] ORDER BY [ItemId] FOR JSON PATH, INCLUDE_NULL_VALUES) AS [snapshots],
                (SELECT * FROM [Inventory].[ItemPhotos] ORDER BY [Id] FOR JSON PATH, INCLUDE_NULL_VALUES) AS [photos],
                (SELECT * FROM [Inventory].[ItemPhotoOperations] ORDER BY [Id] FOR JSON PATH, INCLUDE_NULL_VALUES) AS [operations],
                (SELECT * FROM [Storage].[Attachments] ORDER BY [Id] FOR JSON PATH, INCLUDE_NULL_VALUES) AS [attachments],
                (SELECT * FROM [Storage].[Revisions] ORDER BY [Id] FOR JSON PATH, INCLUDE_NULL_VALUES) AS [revisions],
                (SELECT * FROM [Storage].[RecoveryFiles] ORDER BY [RevisionId] FOR JSON PATH, INCLUDE_NULL_VALUES) AS [recoveryFiles],
                (SELECT * FROM [Security].[FileRecoveryReports] ORDER BY [ReportId] FOR JSON PATH, INCLUDE_NULL_VALUES) AS [recoveryReports]
            FOR JSON PATH;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var json = new System.Text.StringBuilder();
        while (await reader.ReadAsync())
        {
            json.Append(reader.GetString(0));
        }
        return json.ToString();
    }

    private static Task<int> RestoreAsync(SqlConnection connection, Guid id, byte[] version) =>
        CheckedAsync(connection, id, version, "EXEC [Inventory].[RestoreItem] @Id=@id,@ExpectedVersion=@version");
    private static async Task<SqlConnection> ConnectAsync(SqlTestDatabase database, Guid tenant)
    {
        var connection = new SqlConnection(await database.CreateWebUserAsync());
        await connection.OpenAsync();
        await new TenantContextProof(await database.GetTenantContextProofKeyAsync()).ApplyAsync(connection, tenant, CancellationToken.None);
        return connection;
    }

    private static async Task<byte[]> SeedAsync(SqlConnection admin, Guid tenant, Guid id)
    {
        await using var command = new SqlCommand("""
            INSERT [Inventory].[Items] ([Id],[TenantId],[TrackingKind],[Name],[Notes],[StorageLocation],[CreatedAtUtc],[CreationRequestId])
            VALUES (@id,@tenant,'Individual',N'Original',N'Retained notes',N'Tray A',SYSUTCDATETIME(),NEWID());
            SELECT [RowVersion] FROM [Inventory].[Items] WHERE [Id]=@id;
            """, admin);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@tenant", tenant);
        return (byte[])(await command.ExecuteScalarAsync())!;
    }

    private static async Task<(string Fields, byte[] Version, DateTimeOffset? ArchivedAtUtc)> RecordAsync(SqlConnection connection, Guid id)
    {
        await using var command = new SqlCommand("""
            SELECT (SELECT [Id],[TenantId],[TrackingKind],[Name],[Notes],[StorageLocation],[CreatedAtUtc],[CreationRequestId],[CurrentPhotoId]
                FROM [Inventory].[Items] WHERE [Id]=@id FOR JSON PATH, INCLUDE_NULL_VALUES), [RowVersion], [ArchivedAtUtc]
            FROM [Inventory].[Items] WHERE [Id]=@id;
            """, connection);
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), (byte[])reader[1], reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
    }

    private static Task<int> ArchiveAsync(SqlConnection connection, Guid id, byte[] version) =>
        CheckedAsync(connection, id, version, "EXEC [Inventory].[ArchiveItem] @Id=@id,@ExpectedVersion=@version");

    private static Task<int> EditAsync(SqlConnection connection, Guid id, byte[] version) =>
        CheckedAsync(connection, id, version, "EXEC [Inventory].[UpdateItemDetails] @Id=@id,@ExpectedVersion=@version,@Name=N'Edited',@Notes=NULL,@Location=NULL");

    private static async Task<int> CheckedAsync(SqlConnection connection, Guid id, byte[] version, string sql)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@version", version);
        var result = (int)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return result;
    }

    private static async Task<bool> PhotoAsync(SqlConnection connection, Guid id, byte[] version)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = new SqlCommand("EXEC [Inventory].[SetItemPhoto] @Id=@id,@ExpectedVersion=@version,@PhotoId=NULL", connection, transaction);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@version", version);
        try
        {
            await command.ExecuteScalarAsync();
            await transaction.CommitAsync();
            return true;
        }
        catch (SqlException exception) when (exception.Number == 50040)
        {
            await transaction.RollbackAsync();
            return false;
        }
    }
}
