// Copyright (c) 2026 The White Stag Collection.

using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Server.Identity;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Operations;
using Workbench.Server.Storage;
using Workbench.Server.Tenancy;
using Xunit;
using static Workbench.Server.IntegrationTests.ItemPhotoEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ItemPackagePhotoTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData("active", false)]
    [InlineData("all", true)]
    public async Task ScopeIncludesExactStoredDetailBytesAndExplicitAbsenceWithoutForeignPhotos(string scope, bool includesArchive)
    {
        // GIVEN active, archived, unphotographed and foreign items with distinct photographs.
        await using var context = await TestContext.CreateAsync(sqlServer);
        var (_, active) = await context.CreateAsync("=蓝../active", photo: true);
        var (archivePath, archived) = await context.CreateAsync("Archived photo", photo: true, blue: true);
        var (_, absent) = await context.CreateAsync("No photograph", photo: false);
        using var foreign = context.Factory.CreateClient();
        await LoginAsync(foreign, "other@example.com");
        var (_, foreignItem) = await context.CreateAsync("Foreign secret", photo: true, client: foreign);
        var activeBytes = await DetailBytesAsync(context.Client, active);
        var archivedBytes = await DetailBytesAsync(context.Client, archived);
        Assert.Equal(HttpStatusCode.OK, (await SendJsonAsync(context.Client, HttpMethod.Post, archivePath + "/archive",
            new { expectedVersion = Version(archived) })).StatusCode);

        // WHEN preparing either whole-collection scope.
        using var response = await context.ExportAsync(scope);

        // THEN each selected photo is the stored detail object and the manifest names no foreign object.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        using var manifest = JsonDocument.Parse(await ItemPackageEndpointTests.ReadAsync(zip, "manifest.json"));
        var items = manifest.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(includesArchive ? 3 : 2, items.Length);
        await AssertPhotoAsync(zip, items, active, activeBytes);
        var noPhoto = Assert.Single(items, item => Id(item) == absent.GetProperty("id").GetGuid()).GetProperty("photo");
        Assert.Equal("none", noPhoto.GetProperty("status").GetString());
        Assert.Single(noPhoto.EnumerateObject());
        if (includesArchive) await AssertPhotoAsync(zip, items, archived, archivedBytes);
        else Assert.DoesNotContain(items, item => Id(item) == archived.GetProperty("id").GetGuid());
        Assert.DoesNotContain(items, item => Id(item) == foreignItem.GetProperty("id").GetGuid());
        Assert.Equal(includesArchive ? 2 : 1, zip.Entries.Count(entry => entry.FullName.StartsWith("photos/", StringComparison.Ordinal)));
        Assert.DoesNotContain("Foreign secret", await ItemPackageEndpointTests.ReadAsync(zip, "records.csv"));
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains("thumbnail", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("truncated")]
    [InlineData("corrupt")]
    [InlineData("unreadable")]
    [InlineData("overlong")]
    [InlineData("credential")]
    public async Task RequiredPhotoFailureNeverBecomesAbsenceAndFreshRetryRecovers(string failure)
    {
        // GIVEN a current photo whose provider fails or returns bytes inconsistent with its immutable metadata.
        await using var context = await TestContext.CreateAsync(sqlServer);
        var (_, item) = await context.CreateAsync("Private photograph", photo: true);
        var expected = await DetailBytesAsync(context.Client, item);
        context.Store.Failure = failure;

        // WHEN preparing a package that requires the damaged object.
        using var failed = await context.ExportAsync();

        // THEN no ZIP or attachment is released and no private details appear in the error.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.NotEqual("application/zip", failed.Content.Headers.ContentType?.MediaType);
        Assert.Null(failed.Content.Headers.ContentDisposition);
        var error = await failed.Content.ReadAsStringAsync();
        Assert.Contains("export_preparation_failed", error);
        Assert.DoesNotContain("Private photograph", error);
        Assert.DoesNotContain("Injected", error);

        // WHEN storage becomes readable and a new preparation is requested.
        context.Store.Failure = null;
        using var retry = await context.ExportAsync();

        // THEN released preparation capacity permits a complete package containing the exact photo.
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await retry.Content.ReadAsByteArrayAsync()));
        using var manifest = JsonDocument.Parse(await ItemPackageEndpointTests.ReadAsync(zip, "manifest.json"));
        await AssertPhotoAsync(zip, manifest.RootElement.GetProperty("items").EnumerateArray().ToArray(), item, expected);
    }

    [Fact]
    public async Task RecoveryUnavailableDetailFailsBeforeReadingTheProvider()
    {
        // GIVEN an existing photo whose detail revision is marked unavailable by accepted recovery.
        await using var context = await TestContext.CreateAsync(sqlServer);
        var (_, item) = await context.CreateAsync("Recovery photo", photo: true);
        await using var sql = new SqlConnection(context.Application.AdminConnectionString);
        await sql.OpenAsync();
        await using var unavailable = new SqlCommand("""
            INSERT [Storage].[RecoveryFiles] ([TenantId],[RevisionId],[ReportId],[Generation],[Reason],[AcceptedAtUtc])
            SELECT r.[TenantId],r.[Id],NEWID(),1,'Missing',SYSUTCDATETIME()
            FROM [Inventory].[ItemPhotos] p
            JOIN [Storage].[Attachments] a ON a.[Id]=p.[DetailAttachmentId]
            JOIN [Storage].[Revisions] r ON r.[Id]=a.[CurrentRevisionId]
            WHERE p.[Id]=@photo;
            """, sql);
        unavailable.Parameters.AddWithValue("@photo", item.GetProperty("photo").GetProperty("id").GetGuid());
        Assert.Equal(1, await unavailable.ExecuteNonQueryAsync());
        var previousReads = context.Store.Reads;

        // WHEN preparing a package while the physical object still happens to exist.
        using var response = await context.ExportAsync();

        // THEN authoritative recovery metadata prevents delivery and avoids any blob read.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(previousReads, context.Store.Reads);
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.Equal("export_preparation_failed", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementOrRemovalAfterSnapshotCommitsWhileCapturedPhotoRemainsRetained(bool remove)
    {
        // GIVEN a captured current photograph and an independent authenticated writer.
        await using var context = await TestContext.CreateAsync(sqlServer);
        var (path, item) = await context.CreateAsync("Captured photo", photo: true);
        var original = await DetailBytesAsync(context.Client, item);
        using var writer = context.Factory.CreateClient();
        await LoginAsync(writer);
        context.Store.PauseNextRead();
        var exporting = context.ExportAsync();
        try
        {
            await context.Store.Held.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // WHEN replacement/removal commits while package blob IO is paused after metadata capture.
            using var mutation = await (remove
                ? SendJsonAsync(writer, HttpMethod.Delete, path + "/photo", new { requestId = Guid.NewGuid(), expectedVersion = Version(item) })
                : UploadAsync(writer, path, Version(item), Guid.NewGuid(), PhotoFixture.Png(red: 0, blue: 255)))
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(HttpStatusCode.OK, mutation.StatusCode);

            // THEN SQL locks no longer block the writer, and retirement grants a full seven-day grace.
            await using var sql = new SqlConnection(context.Application.AdminConnectionString);
            await sql.OpenAsync();
            await using var retention = new SqlCommand("""
                SELECT COUNT(*) FROM [Storage].[Attachments]
                WHERE [DeletedAtUtc] IS NOT NULL AND [DeleteAfterUtc] >= DATEADD(day, 7, [DeletedAtUtc]);
                """, sql);
            Assert.Equal(2, Convert.ToInt32(await retention.ExecuteScalarAsync()));

            // AND even prematurely available cleanup work cannot purge a captured attachment within its grace.
            await using var early = new SqlCommand("UPDATE [Operations].[WorkItems] SET [AvailableAtUtc]=DATEADD(day,-1,SYSUTCDATETIME()) WHERE [Kind]=1", sql);
            await early.ExecuteNonQueryAsync();
            var processor = new WorkProcessor(await context.Application.CreateWorkerConnectionAsync(),
                context.Factory.Services.GetRequiredService<TenantContextProof>(), new EphemeralDataProtectionProvider(),
                new DisabledIdentityMessageDelivery(), new Dictionary<string, IBlobStore> { [context.Store.Alias] = context.Store });
            Assert.True(await processor.RunOnceAsync(CancellationToken.None));
            Assert.Equal(0, context.Store.Deletions);
        }
        finally { context.Store.Release.TrySetResult(); }

        // THEN the in-flight package includes the old captured bytes despite the current link changing.
        using var response = await exporting;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        using var manifest = JsonDocument.Parse(await ItemPackageEndpointTests.ReadAsync(zip, "manifest.json"));
        await AssertPhotoAsync(zip, manifest.RootElement.GetProperty("items").EnumerateArray().ToArray(), item, original);

        // AND retry snapshots the new state instead of reusing the prior photograph.
        using var next = await context.ExportAsync();
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        using var nextZip = new ZipArchive(new MemoryStream(await next.Content.ReadAsByteArrayAsync()));
        using var nextManifest = JsonDocument.Parse(await ItemPackageEndpointTests.ReadAsync(nextZip, "manifest.json"));
        var nextItems = nextManifest.RootElement.GetProperty("items").EnumerateArray().ToArray();
        if (remove)
        {
            Assert.Equal("none", Assert.Single(nextItems).GetProperty("photo").GetProperty("status").GetString());
            Assert.DoesNotContain(nextZip.Entries, entry => entry.FullName.StartsWith("photos/", StringComparison.Ordinal));
        }
        else
        {
            var current = await writer.GetFromJsonAsync<JsonElement>(path);
            var replacement = await DetailBytesAsync(writer, current);
            Assert.False(original.SequenceEqual(replacement));
            await AssertPhotoAsync(nextZip, nextItems, current, replacement);
        }
    }

    [Fact]
    public async Task RevocationDuringPhotoCopyDeniesTheFullyPreparedPackage()
    {
        // GIVEN authenticated preparation paused during captured photo retrieval.
        await using var context = await TestContext.CreateAsync(sqlServer);
        await context.CreateAsync("Secret photo", photo: true);
        context.Store.PauseNextRead();
        var exporting = context.ExportAsync();
        try
        {
            await context.Store.Held.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // WHEN an independent administrative connection revokes the requesting user's sessions.
            await using var sql = new SqlConnection(context.Application.AdminConnectionString);
            await sql.OpenAsync();
            await using var revoke = new SqlCommand("UPDATE [Identity].[Sessions] SET [RevokedAtUtc]=SYSUTCDATETIME(), [RevocationReason]=N'test' WHERE [UserId]=@user", sql);
            revoke.Parameters.AddWithValue("@user", AuthTestApplication.MemberUserId);
            await revoke.ExecuteNonQueryAsync();
        }
        finally { context.Store.Release.TrySetResult(); }

        // THEN the final authority check prevents any prepared ZIP from being delivered.
        using var response = await exporting;
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.NotEqual("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("Secret photo", await response.Content.ReadAsStringAsync());
    }

    private static Guid Id(JsonElement item) => item.GetProperty("item_id").GetGuid();
    private static string Version(JsonElement item) => item.GetProperty("version").GetString()!;
    private static Task<byte[]> DetailBytesAsync(HttpClient client, JsonElement item) =>
        client.GetByteArrayAsync(item.GetProperty("photo").GetProperty("detailUrl").GetString());

    private static async Task AssertPhotoAsync(ZipArchive zip, JsonElement[] items, JsonElement expected, byte[] bytes)
    {
        var id = expected.GetProperty("id").GetGuid();
        var item = Assert.Single(items, item => Id(item) == id);
        Assert.Equal(expected.GetProperty("name").GetString(), item.GetProperty("name").GetString());
        var photo = item.GetProperty("photo");
        Assert.Equal("included", photo.GetProperty("status").GetString());
        Assert.Equal($"photos/{id:D}.webp", photo.GetProperty("path").GetString());
        Assert.Equal("image/webp", photo.GetProperty("media_type").GetString());
        Assert.Equal(bytes.LongLength, photo.GetProperty("byte_length").GetInt64());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), photo.GetProperty("sha256").GetString(), ignoreCase: true);
        var entry = zip.GetEntry(photo.GetProperty("path").GetString()!);
        Assert.NotNull(entry);
        await using var stream = entry.Open();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        Assert.Equal(bytes, buffer.ToArray());
    }

    private sealed class TestContext(AuthTestApplication application, ControlledStore store,
        WebApplicationFactory<Program> factory, HttpClient client) : IAsyncDisposable
    {
        public AuthTestApplication Application { get; } = application;
        public ControlledStore Store { get; } = store;
        public WebApplicationFactory<Program> Factory { get; } = factory;
        public HttpClient Client { get; } = client;

        public static async Task<TestContext> CreateAsync(SqlServerFixture sqlServer)
        {
            var application = await AuthTestApplication.CreateAsync(sqlServer);
            var store = new ControlledStore();
            var factory = application.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBlobStore>();
                services.AddSingleton<IBlobStore>(store);
            }));
            var client = factory.CreateClient();
            await LoginAsync(client);
            return new TestContext(application, store, factory, client);
        }

        public Task<HttpResponseMessage> ExportAsync(string scope = "all") =>
            SendJsonAsync(Client, HttpMethod.Post, "/api/items/export-package", new { scope });

        public async Task<(string Path, JsonElement Item)> CreateAsync(string name, bool photo, bool blue = false, HttpClient? client = null)
        {
            client ??= Client;
            using var created = await SendJsonAsync(client, HttpMethod.Post, "/api/items", new { creationRequestId = Guid.NewGuid(), name });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var path = created.Headers.Location!.ToString();
            var item = await client.GetFromJsonAsync<JsonElement>(path);
            if (photo)
            {
                using var uploaded = await UploadAsync(client, path, Version(item), Guid.NewGuid(),
                    blue ? PhotoFixture.Png(red: 0, blue: 255) : PhotoFixture.Png());
                Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
                item = await client.GetFromJsonAsync<JsonElement>(path);
            }
            return (path, item);
        }

        public async ValueTask DisposeAsync()
        {
            Store.Release.TrySetResult();
            Client.Dispose();
            await Factory.DisposeAsync();
            await Application.DisposeAsync();
            Store.Dispose();
        }
    }

    private sealed class ControlledStore : IBlobStore, IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "workbench-package-photos-" + Guid.NewGuid().ToString("N"));
        private readonly FileSystemBlobStore _inner;
        private int _pauseRead;
        public ControlledStore()
        {
            Directory.CreateDirectory(_root);
            _inner = new FileSystemBlobStore(_root);
        }
        public string Alias => _inner.Alias;
        public string? Failure { get; set; }
        public int Deletions { get; private set; }
        public int Reads { get; private set; }
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void PauseNextRead() => Interlocked.Exchange(ref _pauseRead, 1);
        public Task<BlobContentIdentity> StageAsync(BlobObjectId id, Stream content, long maximumBytes, CancellationToken cancellationToken) =>
            _inner.StageAsync(id, content, maximumBytes, cancellationToken);
        public Task PublishAsync(BlobObjectId id, CancellationToken cancellationToken) => _inner.PublishAsync(id, cancellationToken);
        public async Task<Stream> OpenReadAsync(BlobObjectId id, CancellationToken cancellationToken)
        {
            Reads++;
            if (Interlocked.Exchange(ref _pauseRead, 0) == 1)
            {
                Held.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            if (Failure == "missing") throw new FileNotFoundException("Injected missing object.");
            if (Failure == "unreadable") throw new IOException("Injected read failure.");
            if (Failure == "credential") throw new Azure.Identity.AuthenticationFailedException("Injected credential failure.");
            var stream = await _inner.OpenReadAsync(id, cancellationToken);
            if (Failure is null) return stream;
            await using (stream)
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                var bytes = buffer.ToArray();
                if (Failure == "truncated") bytes = bytes[..^1];
                else if (Failure == "overlong") bytes = [.. bytes, 0];
                else bytes[^1] ^= 1;
                return new MemoryStream(bytes, writable: false);
            }
        }
        public Task DeleteAsync(BlobObjectId id, CancellationToken cancellationToken)
        {
            Deletions++;
            return _inner.DeleteAsync(id, cancellationToken);
        }
        public IAsyncEnumerable<BlobObjectId> ListAsync(CancellationToken cancellationToken) => _inner.ListAsync(cancellationToken);
        public Task CheckReadyAsync(CancellationToken cancellationToken) => _inner.CheckReadyAsync(cancellationToken);
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
