// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Server.Identity;
using Workbench.Server.Inventory;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Operations;
using Workbench.Server.Persistence;
using Workbench.Server.Storage;
using Workbench.Server.Tenancy;
using Xunit;
using static Workbench.Server.IntegrationTests.ItemPhotoEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ItemPhotoDatabaseTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task AcceptedRecoveryLossReturnsStableAuthorizedNotice()
    {
        // GIVEN a photo whose unavailable disposition was persisted by recovery.
        await using var context = await PhotoDatabaseContext.CreateAsync(sqlServer);
        var (path, item) = await context.CreatePhotoAsync();
        await using var sql = new SqlConnection(context.Application.AdminConnectionString);
        await sql.OpenAsync();
        await using var mark = new SqlCommand("""
            INSERT [Storage].[RecoveryFiles] (TenantId,RevisionId,ReportId,Generation,Reason,AcceptedAtUtc)
                SELECT TenantId,Id,NEWID(),1,'Missing',SYSUTCDATETIME() FROM [Storage].[Revisions] WHERE State=1;
            """, sql);
        await mark.ExecuteNonQueryAsync();
        // WHEN an authorized user requests the unavailable photo.
        var url = item.GetProperty("photo").GetProperty("detailUrl").GetString();
        var response = await context.Client.GetAsync(url);
        // THEN it receives a stable recovery-specific error while its item remains intact.
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("file_unavailable_after_recovery", problem.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await context.Client.GetAsync(path)).StatusCode);
        // AND anonymous callers cannot discover the recovery disposition.
        using var anonymous = context.Application.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task EditingUpgradePreservesRetainedPhotoAndTextThenAllowsCheckedEditing()
    {
        // GIVEN a persisted item and complete photo on the PR base schema.
        await using var context = await PhotoDatabaseContext.CreateAsync(sqlServer, "AddItemPhotographs");
        var (path, version, photoId, bytes) = await context.SeedBasePhotoAsync();
        // WHEN the additive editing migration is applied.
        await DatabaseMigrator.MigrateAsync(context.Application.AdminConnectionString, CancellationToken.None);
        // THEN its text, version, photo metadata, and photo bytes remain intact.
        var retained = await context.Client.GetFromJsonAsync<JsonElement>(path);
        Assert.Equal("Recovery sapphire", retained.GetProperty("name").GetString());
        Assert.Equal(version, retained.GetProperty("version").GetString());
        Assert.Equal(photoId, retained.GetProperty("photo").GetProperty("id").GetGuid());
        Assert.Equal(12, retained.GetProperty("photo").GetProperty("width").GetInt32());
        Assert.Equal(8, retained.GetProperty("photo").GetProperty("height").GetInt32());
        var photo = retained.GetProperty("photo").GetRawText();
        Assert.Equal(bytes, await context.Client.GetByteArrayAsync(retained.GetProperty("photo").GetProperty("detailUrl").GetString()));
        // AND a text edit preserves the photo while advancing the shared version.
        var response = await SendJsonAsync(context.Client, HttpMethod.Put, path,
            new { expectedVersion = retained.GetProperty("version").GetString(), name = "Updated sapphire" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(photo, saved.GetProperty("photo").GetRawText());
        Assert.NotEqual(retained.GetProperty("version").GetString(), saved.GetProperty("version").GetString());
    }

    [Fact]
    public async Task UpgradePreservesExistingItemAndAddsAnEmptyPhotoHistory()
    {
        // GIVEN the immediately preceding schema with a saved item and descriptive content.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer, priorMigration: "AddCollectionNotebook");
        await using var sql = new SqlConnection(application.AdminConnectionString);
        await sql.OpenAsync();
        var id = Guid.NewGuid();
        await using (var seed = new SqlCommand("""
            INSERT INTO [Inventory].[Items]
                ([Id], [TenantId], [TrackingKind], [Name], [Notes], [StorageLocation], [CreatedAtUtc], [CreationRequestId])
            VALUES (@id, @tenant, 'Individual', N'Existing sapphire', N'Original notes', N'Tray A', SYSUTCDATETIME(), NEWID());
            """, sql))
        {
            seed.Parameters.AddWithValue("@id", id);
            seed.Parameters.AddWithValue("@tenant", AuthTestApplication.TenantId);
            await seed.ExecuteNonQueryAsync();
        }
        using var client = application.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);

        // WHEN the additive photograph migration is applied.
        await DatabaseMigrator.MigrateAsync(application.AdminConnectionString, CancellationToken.None);

        // THEN readiness succeeds and the saved item remains intact with no invented photograph.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        await LoginAsync(client);
        var item = await client.GetFromJsonAsync<JsonElement>("/api/items/" + id);
        Assert.Equal("Existing sapphire", item.GetProperty("name").GetString());
        Assert.Equal("Original notes", item.GetProperty("notes").GetString());
        Assert.Equal("Tray A", item.GetProperty("location").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("photo").ValueKind);
        await using var count = new SqlCommand("SELECT (SELECT COUNT(*) FROM [Inventory].[ItemPhotos]) + (SELECT COUNT(*) FROM [Inventory].[ItemPhotoOperations])", sql);
        Assert.Equal(0, Convert.ToInt32(await count.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task RawSqlTenantIsolationAndPointerConstraintsPreservePhotoOwnership()
    {
        // GIVEN a committed photo and ordinary web SQL connections carrying different valid tenant proofs.
        await using var context = await PhotoDatabaseContext.CreateAsync(sqlServer);
        var (path, item) = await context.CreatePhotoAsync();
        var itemId = item.GetProperty("id").GetGuid();
        var photoId = item.GetProperty("photo").GetProperty("id").GetGuid();
        var proof = context.Factory.Services.GetRequiredService<TenantContextProof>();
        await using var foreign = new SqlConnection(context.Application.WebConnectionString);
        await foreign.OpenAsync();
        await proof.ApplyAsync(foreign, Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), CancellationToken.None);

        // WHEN raw SQL bypasses ORM filters and attempts to read or mutate foreign photo records.
        await using (var read = new SqlCommand("SELECT (SELECT COUNT(*) FROM [Inventory].[ItemPhotos]) + (SELECT COUNT(*) FROM [Inventory].[ItemPhotoOperations])", foreign))
        {
            Assert.Equal(0, Convert.ToInt32(await read.ExecuteScalarAsync()));
        }
        await using (var update = new SqlCommand("UPDATE [Inventory].[ItemPhotoOperations] SET [State]=[State] WHERE [ItemId]=@item", foreign))
        {
            update.Parameters.AddWithValue("@item", itemId);
            Assert.Equal(0, await update.ExecuteNonQueryAsync());
        }
        await using (var insert = new SqlCommand("""
            INSERT INTO [Inventory].[ItemPhotoOperations]
                ([Id], [TenantId], [ItemId], [RequestId], [ExpectedVersion], [PayloadSha256], [Kind], [State], [ActorUserId], [CreatedAtUtc])
            VALUES (NEWID(), @tenant, @item, NEWID(), @version, @digest, 1, 0, @actor, SYSUTCDATETIME());
            """, foreign))
        {
            insert.Parameters.AddWithValue("@tenant", AuthTestApplication.TenantId);
            insert.Parameters.AddWithValue("@item", itemId);
            insert.Parameters.AddWithValue("@version", Convert.FromBase64String(item.GetProperty("version").GetString()!));
            insert.Parameters.AddWithValue("@digest", new string('0', 64));
            insert.Parameters.AddWithValue("@actor", AuthTestApplication.MemberUserId);
            Assert.Equal(33504, (await Assert.ThrowsAsync<SqlException>(() => insert.ExecuteNonQueryAsync())).Number);
        }

        // AND an owner tries to attach another owned item's photo through both the SQL constraint and runtime procedure.
        var created = await SendJsonAsync(context.Client, HttpMethod.Post, "/api/items",
            new { creationRequestId = Guid.NewGuid(), name = "Other sapphire" });
        var second = await context.Client.GetFromJsonAsync<JsonElement>(created.Headers.Location!.ToString());
        await using var admin = new SqlConnection(context.Application.AdminConnectionString);
        await admin.OpenAsync();
        await using (var mismatch = new SqlCommand("UPDATE [Inventory].[Items] SET [CurrentPhotoId]=@photo WHERE [Id]=@item", admin))
        {
            mismatch.Parameters.AddWithValue("@photo", photoId);
            mismatch.Parameters.AddWithValue("@item", second.GetProperty("id").GetGuid());
            Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => mismatch.ExecuteNonQueryAsync())).Number);
        }
        await using var owner = new SqlConnection(context.Application.WebConnectionString);
        await owner.OpenAsync();
        await proof.ApplyAsync(owner, AuthTestApplication.TenantId, CancellationToken.None);
        await using (var transaction = (SqlTransaction)await owner.BeginTransactionAsync())
        {
            await using var mismatch = new SqlCommand("EXEC [Inventory].[SetItemPhoto] @Id=@item, @ExpectedVersion=@version, @PhotoId=@photo", owner, transaction);
            mismatch.Parameters.AddWithValue("@item", second.GetProperty("id").GetGuid());
            mismatch.Parameters.AddWithValue("@version", Convert.FromBase64String(second.GetProperty("version").GetString()!));
            mismatch.Parameters.AddWithValue("@photo", photoId);
            Assert.Equal(50040, (await Assert.ThrowsAsync<SqlException>(() => mismatch.ExecuteNonQueryAsync())).Number);
            await transaction.RollbackAsync();
        }

        // THEN the narrow procedure grant has not weakened the existing item text/update/delete denial.
        foreach (var statement in new[]
        {
            "UPDATE [Inventory].[Items] SET [Name]=N'Overwritten' WHERE [Id]=@item",
            "UPDATE [Inventory].[Items] SET [CurrentPhotoId]=NULL WHERE [Id]=@item",
            "DELETE FROM [Inventory].[Items] WHERE [Id]=@item",
            "UPDATE [Inventory].[ItemPhotos] SET [Width]=1 WHERE [ItemId]=@item",
            "DELETE FROM [Inventory].[ItemPhotos] WHERE [ItemId]=@item",
        })
        {
            await using var denied = new SqlCommand(statement, owner);
            denied.Parameters.AddWithValue("@item", itemId);
            Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => denied.ExecuteNonQueryAsync())).Number);
        }
        var current = await context.Client.GetFromJsonAsync<JsonElement>(path);
        Assert.Equal(photoId, current.GetProperty("photo").GetProperty("id").GetGuid());
        Assert.Equal("Recovery sapphire", current.GetProperty("name").GetString());
    }

    [Fact]
    public async Task RemovedPhotoExportsBothVariantsAndHoldPreventsPurgeUntilReleased()
    {
        // GIVEN a removed photo whose two immutable variants remain in the retained manifest.
        await using var context = await PhotoDatabaseContext.CreateAsync(sqlServer);
        var (path, item) = await context.CreatePhotoAsync();
        Assert.Equal(HttpStatusCode.OK, (await SendJsonAsync(context.Client, HttpMethod.Delete, path + "/photo",
            new { requestId = Guid.NewGuid(), expectedVersion = item.GetProperty("version").GetString() })).StatusCode);
        var entries = await StorageMaintenanceCommand.ReadEntriesAsync(context.Application.AdminConnectionString, CancellationToken.None);
        Assert.Equal(2, entries.Count);
        foreach (var entry in entries)
        {
            await BlobMaintenance.VerifyAsync(context.Store, entry, CancellationToken.None);
        }
        await using var sql = new SqlConnection(context.Application.AdminConnectionString);
        await sql.OpenAsync();
        await using (var hold = new SqlCommand("""
            UPDATE [Storage].[Attachments] SET [Held]=1, [DeleteAfterUtc]=DATEADD(day, -1, SYSUTCDATETIME());
            UPDATE [Operations].[WorkItems] SET [AvailableAtUtc]=DATEADD(day, -1, SYSUTCDATETIME());
            """, sql))
        {
            await hold.ExecuteNonQueryAsync();
        }
        var worker = new WorkProcessor(await context.Application.CreateWorkerConnectionAsync(),
            context.Factory.Services.GetRequiredService<TenantContextProof>(), new EphemeralDataProtectionProvider(),
            new DisabledIdentityMessageDelivery(), new Dictionary<string, IBlobStore> { [context.Store.Alias] = context.Store });

        // WHEN both deletion jobs run after the grace deadline with a hold in force.
        Assert.True(await worker.RunOnceAsync(CancellationToken.None));
        Assert.True(await worker.RunOnceAsync(CancellationToken.None));

        // THEN neither variant is deleted, and explicitly releasing the hold and requeuing permits both purges.
        foreach (var entry in entries)
        {
            await BlobMaintenance.VerifyAsync(context.Store, entry, CancellationToken.None);
        }
        await using (var release = new SqlCommand("""
            UPDATE [Storage].[Attachments] SET [Held]=0;
            UPDATE [Operations].[WorkItems] SET [State]=0, [AvailableAtUtc]=DATEADD(day, -1, SYSUTCDATETIME()) WHERE [Kind]=1;
            """, sql))
        {
            await release.ExecuteNonQueryAsync();
        }
        Assert.True(await worker.RunOnceAsync(CancellationToken.None));
        Assert.True(await worker.RunOnceAsync(CancellationToken.None));
        foreach (var entry in entries)
        {
            await Assert.ThrowsAsync<FileNotFoundException>(() => context.Store.OpenReadAsync(
                new BlobObjectId(entry.TenantId, entry.RevisionId), CancellationToken.None));
        }
        Assert.Empty(await StorageMaintenanceCommand.ReadEntriesAsync(context.Application.AdminConnectionString, CancellationToken.None));
        await using var purged = new SqlCommand("SELECT COUNT(*) FROM [Storage].[Revisions] WHERE [State]=3", sql);
        Assert.Equal(2, Convert.ToInt32(await purged.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task PairedSnapshotRequiresBothPhotoVariantsBeforeRecoveryCanReopenReadiness()
    {
        // GIVEN a committed photo and a verified snapshot containing both variant identities.
        await using var context = await PhotoDatabaseContext.CreateAsync(sqlServer);
        await context.CreatePhotoAsync();
        var snapshotPath = Directory.CreateDirectory(Path.Combine(context.Root, "snapshot")).FullName;
        var snapshot = new FileSystemBlobStore(snapshotPath);
        var entries = await StorageMaintenanceCommand.ReadEntriesAsync(context.Application.AdminConnectionString, CancellationToken.None);
        Assert.Equal(2, entries.Count);
        foreach (var entry in entries)
        {
            await BlobMaintenance.CopyAsync(context.Store, snapshot, entry, CancellationToken.None);
        }
        var databaseName = new SqlConnectionStringBuilder(context.Application.AdminConnectionString).InitialCatalog;
        var configPath = Path.Combine(context.Root, "maintenance.json");
        var manifestPath = Path.Combine(context.Root, "manifest.json");
        var arguments = new Dictionary<string, string>
        {
            ["--offline-confirmation"] = "OFFLINE " + databaseName,
            ["--config-file"] = configPath,
            ["--output-file"] = manifestPath,
            ["--manifest-file"] = manifestPath,
        };
        await StorageMaintenanceCommand.RunAsync("manifest", context.Application.AdminConnectionString, databaseName, arguments, CancellationToken.None);

        // WHEN a paired recovery is pending and one required variant is missing from the restored provider.
        await using (var sql = new SqlConnection(context.Application.AdminConnectionString))
        {
            await sql.OpenAsync();
            await using var pending = new SqlCommand("UPDATE [Security].[BlobRecoveryState] SET [IsPending]=1", sql);
            await pending.ExecuteNonQueryAsync();
        }
        var missing = entries[1];
        await context.Store.DeleteAsync(new BlobObjectId(missing.TenantId, missing.RevisionId), CancellationToken.None);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await context.Client.GetAsync("/health/ready")).StatusCode);
        await Assert.ThrowsAsync<FileNotFoundException>(() => StorageMaintenanceCommand.RunAsync("verify",
            context.Application.AdminConnectionString, databaseName, arguments, CancellationToken.None));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await context.Client.GetAsync("/health/ready")).StatusCode);

        // THEN recovering and verifying the missing variant permits readiness and preserves both digests.
        await BlobMaintenance.CopyAsync(snapshot, context.Store, missing, CancellationToken.None);
        await StorageMaintenanceCommand.RunAsync("verify", context.Application.AdminConnectionString, databaseName, arguments, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, (await context.Client.GetAsync("/health/ready")).StatusCode);
        foreach (var entry in entries)
        {
            await BlobMaintenance.VerifyAsync(context.Store, entry, CancellationToken.None);
        }
    }

    private sealed class PhotoDatabaseContext(AuthTestApplication application, string root,
        FileSystemBlobStore store, WebApplicationFactory<Program> factory, HttpClient client) : IAsyncDisposable
    {
        public AuthTestApplication Application { get; } = application;
        public string Root { get; } = root;
        public string LivePath => Path.Combine(Root, "live");
        public FileSystemBlobStore Store { get; } = store;
        public WebApplicationFactory<Program> Factory { get; } = factory;
        public HttpClient Client { get; } = client;

        public static async Task<PhotoDatabaseContext> CreateAsync(SqlServerFixture sqlServer, string? priorMigration = null)
        {
            var application = await AuthTestApplication.CreateAsync(sqlServer, priorMigration: priorMigration);
            var root = Path.Combine(Path.GetTempPath(), "workbench-photo-database-" + Guid.NewGuid().ToString("N"));
            var live = Directory.CreateDirectory(Path.Combine(root, "live")).FullName;
            var configPath = Path.Combine(root, "maintenance.json");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
            {
                Storage = new { Provider = "FileSystem", Root = live, DurableVolume = true, InstallationId = Guid.NewGuid() },
            }));
            var config = new ConfigurationBuilder().AddJsonFile(configPath).Build();
            var store = (FileSystemBlobStore)OperationalConfiguration.CreateStore(config)!;
            var factory = application.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBlobStore>();
                services.AddSingleton<IBlobStore>(store);
            }));
            var client = factory.CreateClient();
            await LoginAsync(client);
            return new PhotoDatabaseContext(application, root, store, factory, client);
        }

        public async Task<(string Path, string Version, Guid PhotoId, byte[] Detail)> SeedBasePhotoAsync()
        {
            // Seed durable SQL and blobs using the base schema instead of invoking a newer inventory model.
            var itemId = Guid.NewGuid();
            var photoId = Guid.NewGuid();
            var detailRevision = Guid.NewGuid();
            var thumbRevision = Guid.NewGuid();
            var processed = new PhotoProcessor().Process(PhotoFixture.Png());
            async Task<BlobContentIdentity> PersistAsync(Guid revision, byte[] bytes)
            {
                var id = new BlobObjectId(AuthTestApplication.TenantId, revision);
                using var content = new MemoryStream(bytes);
                var identity = await Store.StageAsync(id, content, PhotoProcessor.MaximumBytes, CancellationToken.None);
                await Store.PublishAsync(id, CancellationToken.None);
                return identity;
            }
            var detail = await PersistAsync(detailRevision, processed.Detail);
            var thumb = await PersistAsync(thumbRevision, processed.Thumbnail);
            await using var sql = new SqlConnection(Application.AdminConnectionString);
            await sql.OpenAsync();
            await using var seed = new SqlCommand("""
                DECLARE @detail uniqueidentifier=NEWID(), @thumb uniqueidentifier=NEWID(), @operation uniqueidentifier=NEWID();
                INSERT [Inventory].[Items] ([Id],[TenantId],[TrackingKind],[Name],[CreatedAtUtc],[CreationRequestId])
                    VALUES (@item,@tenant,'Individual',N'Recovery sapphire',SYSUTCDATETIME(),NEWID());
                INSERT [Storage].[Attachments] ([Id],[TenantId],[CreatedAtUtc])
                    VALUES (@detail,@tenant,SYSUTCDATETIME()),(@thumb,@tenant,SYSUTCDATETIME());
                INSERT [Storage].[Revisions]
                    ([Id],[TenantId],[AttachmentId],[OperationId],[ActorUserId],[ProviderAlias],[Source],[MediaType],[Length],[Sha256],[State],[CreatedAtUtc])
                    VALUES (@detailRevision,@tenant,@detail,NEWID(),@actor,@alias,N'ItemPhoto',N'image/webp',@detailLength,@detailDigest,1,SYSUTCDATETIME()),
                        (@thumbRevision,@tenant,@thumb,NEWID(),@actor,@alias,N'ItemPhoto',N'image/webp',@thumbLength,@thumbDigest,1,SYSUTCDATETIME());
                UPDATE [Storage].[Attachments] SET [CurrentRevisionId]=@detailRevision WHERE [Id]=@detail;
                UPDATE [Storage].[Attachments] SET [CurrentRevisionId]=@thumbRevision WHERE [Id]=@thumb;
                DECLARE @version varbinary(8)=(SELECT [RowVersion] FROM [Inventory].[Items] WHERE [Id]=@item);
                INSERT [Inventory].[ItemPhotoOperations]
                    ([Id],[TenantId],[ItemId],[RequestId],[ExpectedVersion],[PayloadSha256],[Kind],[State],[ActorUserId],[CreatedAtUtc],
                        [DetailAttachmentId],[ThumbnailAttachmentId])
                    VALUES (@operation,@tenant,@item,NEWID(),@version,@detailDigest,0,0,@actor,SYSUTCDATETIME(),@detail,@thumb);
                INSERT [Inventory].[ItemPhotos]
                    ([Id],[TenantId],[ItemId],[OperationId],[DetailAttachmentId],[ThumbnailAttachmentId],[Width],[Height],[CreatedAtUtc])
                    VALUES (@photo,@tenant,@item,@operation,@detail,@thumb,@width,@height,SYSUTCDATETIME());
                UPDATE [Inventory].[Items] SET [CurrentPhotoId]=@photo WHERE [Id]=@item;
                SET @version=(SELECT [RowVersion] FROM [Inventory].[Items] WHERE [Id]=@item);
                UPDATE [Inventory].[ItemPhotoOperations] SET [State]=1,[ResultVersion]=@version WHERE [Id]=@operation;
                SELECT @version;
                """, sql);
            seed.Parameters.AddWithValue("@item", itemId);
            seed.Parameters.AddWithValue("@tenant", AuthTestApplication.TenantId);
            seed.Parameters.AddWithValue("@actor", AuthTestApplication.MemberUserId);
            seed.Parameters.AddWithValue("@photo", photoId);
            seed.Parameters.AddWithValue("@detailRevision", detailRevision);
            seed.Parameters.AddWithValue("@thumbRevision", thumbRevision);
            seed.Parameters.AddWithValue("@alias", Store.Alias);
            seed.Parameters.AddWithValue("@detailLength", detail.Length);
            seed.Parameters.AddWithValue("@detailDigest", detail.Sha256);
            seed.Parameters.AddWithValue("@thumbLength", thumb.Length);
            seed.Parameters.AddWithValue("@thumbDigest", thumb.Sha256);
            seed.Parameters.AddWithValue("@width", processed.Width);
            seed.Parameters.AddWithValue("@height", processed.Height);
            var version = (byte[])(await seed.ExecuteScalarAsync())!;
            return ("/api/items/" + itemId, Convert.ToBase64String(version), photoId, processed.Detail);
        }

        public async Task<(string Path, JsonElement Item)> CreatePhotoAsync()
        {
            var created = await SendJsonAsync(Client, HttpMethod.Post, "/api/items",
                new { creationRequestId = Guid.NewGuid(), name = "Recovery sapphire" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var path = created.Headers.Location!.ToString();
            var item = await Client.GetFromJsonAsync<JsonElement>(path);
            Assert.Equal(HttpStatusCode.OK, (await UploadAsync(Client, path, item.GetProperty("version").GetString()!, Guid.NewGuid())).StatusCode);
            return (path, await Client.GetFromJsonAsync<JsonElement>(path));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
            await Application.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }
}
