// Copyright (c) 2026 The White Stag Collection.

using System.Data.Common;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Storage;
using Xunit;
using static Workbench.Server.IntegrationTests.ItemPhotoEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ItemPackagePhotoCaptureTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotLocksHoldConcurrentPhotoMutationUntilItsOriginalRevisionIsCaptured(bool remove)
    {
        // GIVEN an owned current photograph and independent reader/writer sessions using real SQL connections.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var storage = new TestStorage();
        await using var factory = application.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IBlobStore>();
            services.AddSingleton<IBlobStore>(storage.Store);
        }));
        using var writer = factory.CreateClient();
        await LoginAsync(writer);
        using var created = await SendJsonAsync(writer, HttpMethod.Post, "/api/items",
            new { creationRequestId = Guid.NewGuid(), name = "Photo at capture" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var path = created.Headers.Location!.ToString();
        var item = await writer.GetFromJsonAsync<JsonElement>(path);
        using var uploaded = await UploadAsync(writer, path, Version(item), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        item = await writer.GetFromJsonAsync<JsonElement>(path);
        var oldPhotoId = item.GetProperty("photo").GetProperty("id").GetGuid();
        var original = await writer.GetByteArrayAsync(item.GetProperty("photo").GetProperty("detailUrl").GetString());
        var barrier = new BeforeCommit();
        await using var readerFactory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<WorkbenchDbContext>(options => options.AddInterceptors(barrier))));
        using var reader = readerFactory.CreateClient();
        await LoginAsync(reader);
        var exporting = SendJsonAsync(reader, HttpMethod.Post, "/api/items/export-package", new { scope = "all" });

        // WHEN a replacement/removal starts while the SERIALIZABLE capture transaction still holds its locks.
        Task<HttpResponseMessage>? mutation = null;
        try
        {
            await barrier.Held.Task.WaitAsync(TimeSpan.FromSeconds(15));
            mutation = remove
                ? SendJsonAsync(writer, HttpMethod.Delete, path + "/photo", new { requestId = Guid.NewGuid(), expectedVersion = Version(item) })
                : UploadAsync(writer, path, Version(item), Guid.NewGuid(), PhotoFixture.Png(red: 0, blue: 255));
            await ItemExportConcurrencyTests.AssertBlockedWriterAsync(application.AdminConnectionString);
            Assert.False(mutation.IsCompleted);
        }
        finally { barrier.Release.TrySetResult(); }

        // THEN the real blocked writer commits after capture, while the complete package contains the prior bytes.
        using var response = await exporting;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(mutation);
        using var changed = await mutation;
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var id = item.GetProperty("id").GetGuid();
        using var manifest = JsonDocument.Parse(await ItemPackageEndpointTests.ReadAsync(zip, "manifest.json"));
        var captured = Assert.Single(manifest.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(id, captured.GetProperty("item_id").GetGuid());
        var photo = captured.GetProperty("photo");
        Assert.Equal("included", photo.GetProperty("status").GetString());
        Assert.Equal($"photos/{id:D}.webp", photo.GetProperty("path").GetString());
        Assert.Equal(original.LongLength, photo.GetProperty("byte_length").GetInt64());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(original)), photo.GetProperty("sha256").GetString(), ignoreCase: true);
        await using var entry = zip.GetEntry($"photos/{id:D}.webp")!.Open();
        using var bytes = new MemoryStream();
        await entry.CopyToAsync(bytes);
        Assert.Equal(original, bytes.ToArray());

        // AND the current item reflects the successful mutation, independently of the exported snapshot.
        var current = await writer.GetFromJsonAsync<JsonElement>(path);
        Assert.NotEqual(Version(item), Version(current));
        if (remove) Assert.Equal(JsonValueKind.Null, current.GetProperty("photo").ValueKind);
        else
        {
            Assert.NotEqual(oldPhotoId, current.GetProperty("photo").GetProperty("id").GetGuid());
            var replacement = await writer.GetByteArrayAsync(current.GetProperty("photo").GetProperty("detailUrl").GetString());
            Assert.False(original.SequenceEqual(replacement));
        }
    }

    private static string Version(JsonElement item) => item.GetProperty("version").GetString()!;

    private sealed class BeforeCommit : DbTransactionInterceptor
    {
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Held.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return result;
        }
    }

    private sealed class TestStorage : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "workbench-package-capture-" + Guid.NewGuid().ToString("N"));
        public FileSystemBlobStore Store { get; }
        public TestStorage()
        {
            Directory.CreateDirectory(_root);
            Store = new FileSystemBlobStore(_root);
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
