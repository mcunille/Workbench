// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ItemArchivingDatabaseTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task RestrictedArchivePreservesIdentityAndRejectsAllLaterWrites()
    {
        // GIVEN a fresh migrated database and restricted connections for two tenants.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var tenant = Guid.NewGuid();
        var foreignTenant = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, foreignTenant);
        await using var admin = new SqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        var id = Guid.NewGuid();
        var version = await SeedAsync(admin, tenant, id);
        await using var owner = await ConnectAsync(database, tenant);
        await using var foreign = await ConnectAsync(database, foreignTenant);
        var original = await RecordAsync(admin, id);

        // WHEN missing and other-tenant identities are submitted through the checked command.
        Assert.Equal(0, await ArchiveAsync(foreign, id, version));
        Assert.Equal(0, await ArchiveAsync(owner, Guid.NewGuid(), version));
        Assert.Equal(2, await ArchiveAsync(owner, id, new byte[8]));
        var before = DateTimeOffset.UtcNow;
        Assert.Equal(1, await ArchiveAsync(owner, id, version));

        // THEN only archive state and rowversion change, with the timestamp supplied in UTC.
        var current = await RecordAsync(admin, id);
        Assert.Equal(original.Fields, current.Fields);
        Assert.NotEqual(version, current.Version);
        Assert.NotNull(current.ArchivedAtUtc);
        Assert.Equal(TimeSpan.Zero, current.ArchivedAtUtc.Value.Offset);
        Assert.InRange(current.ArchivedAtUtc.Value, before.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(5));
        Assert.Equal(3, await ArchiveAsync(owner, id, version));
        Assert.Equal(3, await ArchiveAsync(owner, id, current.Version));
        Assert.Equal(3, await EditAsync(owner, id, current.Version));
        Assert.False(await PhotoAsync(owner, id, current.Version));
        var unchanged = await RecordAsync(admin, id);
        Assert.Equal(current.Fields, unchanged.Fields);
        Assert.Equal(current.Version, unchanged.Version);
        Assert.Equal(current.ArchivedAtUtc, unchanged.ArchivedAtUtc);

        // AND direct reads retain owner access, RLS hides the row, and runtime permissions prohibit bypass.
        await using var hidden = new SqlCommand("SELECT COUNT(*) FROM [Inventory].[Items] WHERE [Id]=@id", foreign);
        hidden.Parameters.AddWithValue("@id", id);
        Assert.Equal(0, await hidden.ExecuteScalarAsync());
        Assert.Equal(current.Fields, (await RecordAsync(owner, id)).Fields);
        foreach (var statement in new[]
        {
            "UPDATE [Inventory].[Items] SET [ArchivedAtUtc]=NULL",
            "UPDATE [Inventory].[Items] SET [Name]=N'Bypass'",
            "UPDATE [Inventory].[Items] SET [CurrentPhotoId]=NULL",
            "DELETE FROM [Inventory].[Items]",
        })
        {
            await using var denied = new SqlCommand(statement, owner);
            Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => denied.ExecuteNonQueryAsync())).Number);
        }
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("text")]
    [InlineData("photo")]
    public async Task ConcurrentCommandsHaveExactlyOneWinner(string competitor)
    {
        // GIVEN independent restricted SQL connections and one shared item version.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, otherTenant);
        await using var admin = new SqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        await using var first = await ConnectAsync(database, tenant);
        await using var second = await ConnectAsync(database, tenant);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var id = Guid.NewGuid();
            var version = await SeedAsync(admin, tenant, id);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<bool> ArchiveRaceAsync() { await start.Task; return await ArchiveAsync(first, id, version) == 1; }
            async Task<bool> CompetitorAsync()
            {
                await start.Task;
                return competitor switch
                {
                    "archive" => await ArchiveAsync(second, id, version) == 1,
                    "text" => await EditAsync(second, id, version) == 1,
                    _ => await PhotoAsync(second, id, version),
                };
            }

            // WHEN both commands are released concurrently against that version.
            var archive = ArchiveRaceAsync();
            var other = CompetitorAsync();
            start.SetResult();
            var results = await Task.WhenAll(archive, other);

            // THEN exactly one commits and a stale archive cannot alter the winning record.
            Assert.Single(results, succeeded => succeeded);
            var saved = await RecordAsync(admin, id);
            Assert.NotEqual(version, saved.Version);
            Assert.Equal(competitor == "archive" || results[0], saved.ArchivedAtUtc.HasValue);
            Assert.Equal(saved.ArchivedAtUtc.HasValue ? 3 : 2, await ArchiveAsync(first, id, version));
            Assert.Equal(saved.Version, (await RecordAsync(admin, id)).Version);
        }
    }

    [Fact]
    public async Task UpgradeRetainsEditedIdentityPhotosAndCompletedAndPendingOperations()
    {
        // GIVEN SQL-seeded data on the actual PR base schema, without using a newer EF model.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateToAsync(database.AdminConnectionString, "AddItemDetailEditing", CancellationToken.None);
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
        var retained = await RetainedEvidenceAsync(admin);
        await using var token = new SqlCommand("SELECT [RowVersion] FROM [Inventory].[Items] WHERE [Id]=@id", admin);
        token.Parameters.AddWithValue("@id", id);
        var version = (byte[])(await token.ExecuteScalarAsync())!;

        // WHEN the additive migration upgrades the populated base database.
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);

        // THEN all retained records remain byte-for-byte equivalent and existing items are active.
        var item = await RecordAsync(admin, id);
        Assert.Null(item.ArchivedAtUtc);
        Assert.Equal(version, item.Version);
        Assert.Equal(retained, await RetainedEvidenceAsync(admin));
        await using var owner = await ConnectAsync(database, tenant);
        Assert.Equal(1, await ArchiveAsync(owner, id, version));
        var archived = await RecordAsync(admin, id);
        Assert.Equal(item.Fields, archived.Fields);
        Assert.Equal(retained, await RetainedEvidenceAsync(admin));
        Assert.Equal(3, await EditAsync(owner, id, archived.Version));
        Assert.False(await PhotoAsync(owner, id, archived.Version));
        Assert.Equal(retained, await RetainedEvidenceAsync(admin));
        Assert.Equal(archived.Fields, (await RecordAsync(admin, id)).Fields);
        Assert.Equal(retained, await RetainedEvidenceAsync(owner));
        await using var foreign = await ConnectAsync(database, otherTenant);
        await using var hidden = new SqlCommand("""
            SELECT (SELECT COUNT(*) FROM [Inventory].[Items]) + (SELECT COUNT(*) FROM [Inventory].[ItemPhotos])
                + (SELECT COUNT(*) FROM [Inventory].[ItemPhotoOperations]) + (SELECT COUNT(*) FROM [Inventory].[ItemCreationSnapshots])
                + (SELECT COUNT(*) FROM [Storage].[Attachments]) + (SELECT COUNT(*) FROM [Storage].[Revisions]);
            """, foreign);
        Assert.Equal(0, await hidden.ExecuteScalarAsync());
        Assert.Equal(0, await ArchiveAsync(foreign, id, archived.Version));
        Assert.Equal(0, await EditAsync(foreign, id, archived.Version));
        Assert.False(await PhotoAsync(foreign, id, archived.Version));

        // AND destructive rollback is rejected without discarding archive state or photo provenance.
        var migrator = await database.CreateRoleUserAsync("workbench_migrator");
        Assert.Equal(50020, (await Assert.ThrowsAsync<SqlException>(() =>
            DatabaseMigrator.MigrateToAsync(migrator, "AddItemDetailEditing", CancellationToken.None))).Number);
        Assert.Equal(archived.ArchivedAtUtc, (await RecordAsync(admin, id)).ArchivedAtUtc);
        Assert.Equal(retained, await RetainedEvidenceAsync(admin));
    }

    private static async Task<string> RetainedEvidenceAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand("""
            SELECT (SELECT * FROM [Inventory].[ItemCreationSnapshots] ORDER BY [ItemId] FOR JSON PATH, INCLUDE_NULL_VALUES) AS [snapshots],
                (SELECT * FROM [Inventory].[ItemPhotos] ORDER BY [Id] FOR JSON PATH, INCLUDE_NULL_VALUES) AS [photos],
                (SELECT * FROM [Inventory].[ItemPhotoOperations] ORDER BY [Id] FOR JSON PATH, INCLUDE_NULL_VALUES) AS [operations],
                (SELECT * FROM [Storage].[Attachments] ORDER BY [Id] FOR JSON PATH, INCLUDE_NULL_VALUES) AS [attachments],
                (SELECT * FROM [Storage].[Revisions] ORDER BY [Id] FOR JSON PATH, INCLUDE_NULL_VALUES) AS [revisions]
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

    [Fact]
    public async Task ArchiveRequiresTransactionAndExactVersionAndRollsBackAtomically()
    {
        // GIVEN an active item and a restricted connection with its valid rowversion.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, otherTenant);
        await using var admin = new SqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        var id = Guid.NewGuid();
        var version = await SeedAsync(admin, tenant, id);
        await using var owner = await ConnectAsync(database, tenant);

        // WHEN a caller omits the transaction or supplies malformed versions.
        await using (var command = new SqlCommand("EXEC [Inventory].[ArchiveItem] @Id=@id,@ExpectedVersion=@version", owner))
        {
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@version", version);
            Assert.Equal(50043, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync())).Number);
        }
        foreach (var malformed in new[] { Array.Empty<byte>(), new byte[7], version.Concat(new byte[] { 0 }).ToArray() })
        {
            Assert.Equal(50043, (await Assert.ThrowsAsync<SqlException>(() => ArchiveAsync(owner, id, malformed))).Number);
        }
        await using (var transaction = (SqlTransaction)await owner.BeginTransactionAsync())
        {
            await using var command = new SqlCommand("EXEC [Inventory].[ArchiveItem] @Id=@id,@ExpectedVersion=NULL", owner, transaction);
            command.Parameters.AddWithValue("@id", id);
            Assert.Equal(50043, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync())).Number);
            await transaction.RollbackAsync();
        }

        // AND an ordinary INSERT grant cannot forge the archive timestamp at creation.
        await using (var forged = new SqlCommand("""
            INSERT [Inventory].[Items] ([Id],[TenantId],[TrackingKind],[Name],[CreatedAtUtc],[CreationRequestId],[ArchivedAtUtc])
            VALUES (NEWID(),@tenant,'Individual',N'Forged archive',SYSUTCDATETIME(),NEWID(),SYSUTCDATETIME());
            """, owner))
        {
            forged.Parameters.AddWithValue("@tenant", tenant);
            Assert.Equal(50043, (await Assert.ThrowsAsync<SqlException>(() => forged.ExecuteNonQueryAsync())).Number);
        }

        // THEN no rejected command has modified the record.
        var active = await RecordAsync(admin, id);
        Assert.Null(active.ArchivedAtUtc);
        Assert.Equal(version, active.Version);

        // WHEN a valid archive runs but its enclosing transaction rolls back.
        await using (var transaction = (SqlTransaction)await owner.BeginTransactionAsync())
        {
            await using var command = new SqlCommand("EXEC [Inventory].[ArchiveItem] @Id=@id,@ExpectedVersion=@version", owner, transaction);
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@version", version);
            Assert.Equal(1, await command.ExecuteScalarAsync());
            await transaction.RollbackAsync();
        }

        // THEN the original token remains usable and no archive state escapes the transaction.
        var rolledBack = await RecordAsync(admin, id);
        Assert.Null(rolledBack.ArchivedAtUtc);
        Assert.Equal(version, rolledBack.Version);
        Assert.Equal(1, await ArchiveAsync(owner, id, version));
    }

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
