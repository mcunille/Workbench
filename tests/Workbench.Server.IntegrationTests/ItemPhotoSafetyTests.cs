// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Storage;
using Xunit;
using static Workbench.Server.IntegrationTests.ItemPhotoEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ItemPhotoSafetyTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData("unsupported", HttpStatusCode.UnsupportedMediaType)]
    [InlineData("corrupt", HttpStatusCode.UnprocessableEntity)]
    [InlineData("oversized", HttpStatusCode.RequestEntityTooLarge)]
    public async Task InvalidReplacementPreservesTheCurrentPair(string input, HttpStatusCode expected)
    {
        // GIVEN an item with a complete current photograph.
        await using var context = await TestContext.CreateAsync(sqlServer);
        var (path, current) = await context.CreatePhotographedItemAsync();
        var bytes = input switch
        {
            "unsupported" => "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray(),
            "corrupt" => new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0 },
            _ => new byte[4 * 1024 * 1024 + 1],
        };

        // WHEN invalid replacement bytes are submitted.
        var response = await UploadAsync(context.Client, path, Version(current), Guid.NewGuid(), bytes);

        // THEN validation fails before replacing either visible variant.
        Assert.Equal(expected, response.StatusCode);
        await AssertUnchangedAsync(context.Client, path, current);
    }

    [Fact]
    public async Task TenantAndItemBoundariesProtectReadsAndMutations()
    {
        // GIVEN a photograph owned by another tenant and a second item in the owning tenant.
        await using var context = await TestContext.CreateAsync(sqlServer);
        var (path, current) = await context.CreatePhotographedItemAsync();
        var (secondPath, _) = await context.CreateItemAsync();
        using var foreign = context.Factory.CreateClient();
        await LoginAsync(foreign, "other@example.com");

        // WHEN foreign identifiers or a mismatched item/photo pair are used.
        foreach (var variant in new[] { "detail", "thumbnail" })
        {
            var url = current.GetProperty("photo").GetProperty(variant + "Url").GetString()!;
            Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound,
                (await context.Client.GetAsync(url.Replace(path + "/photo/", secondPath + "/photo/", StringComparison.Ordinal))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound,
            (await UploadAsync(foreign, path, Version(current), Guid.NewGuid(), Photo())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendJsonAsync(foreign, HttpMethod.Delete, path + "/photo",
            new { requestId = Guid.NewGuid(), expectedVersion = Version(current) })).StatusCode);

        // THEN no request changes the owner's current photograph.
        await AssertUnchangedAsync(context.Client, path, current);
    }

    [Fact]
    public async Task AnonymousAndMissingAntiforgeryRequestsCannotReadOrMutatePhotos()
    {
        // GIVEN an authenticated owner with a current photograph.
        await using var context = await TestContext.CreateAsync(sqlServer);
        var (path, current) = await context.CreatePhotographedItemAsync();
        using var anonymous = context.Factory.CreateClient();

        // WHEN an anonymous session reads or writes, or an owner omits antiforgery.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync(current.GetProperty("photo").GetProperty("detailUrl").GetString())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await UploadAsync(anonymous, path, Version(current), Guid.NewGuid(), Photo())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendJsonAsync(anonymous, HttpMethod.Delete, path + "/photo",
            new { requestId = Guid.NewGuid(), expectedVersion = Version(current) })).StatusCode);
        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(Photo()), "file", "prepared.png");
        body.Add(new StringContent(Guid.NewGuid().ToString()), "requestId");
        body.Add(new StringContent(Version(current)), "expectedVersion");
        Assert.Equal(HttpStatusCode.BadRequest, (await context.Client.PutAsync(path + "/photo", body)).StatusCode);
        using var remove = new HttpRequestMessage(HttpMethod.Delete, path + "/photo")
        {
            Content = JsonContent.Create(new { requestId = Guid.NewGuid(), expectedVersion = Version(current) }),
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await context.Client.SendAsync(remove)).StatusCode);

        // THEN the current pair remains available to its owner.
        await AssertUnchangedAsync(context.Client, path, current);
    }

    [Fact]
    public async Task StaleMutationsAndPayloadMismatchCannotOverwriteTheWinner()
    {
        // GIVEN a completed replacement based on an older item version.
        await using var context = await TestContext.CreateAsync(sqlServer);
        var (path, old) = await context.CreatePhotographedItemAsync();
        var requestId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK,
            (await UploadAsync(context.Client, path, Version(old), requestId, Photo(blue: true))).StatusCode);
        var current = await context.Client.GetFromJsonAsync<JsonElement>(path);

        // WHEN stale upload/remove commands or a different payload reuse are attempted.
        Assert.Equal(HttpStatusCode.Conflict,
            (await UploadAsync(context.Client, path, Version(old), Guid.NewGuid(), Photo())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await UploadAsync(context.Client, path, Version(old), requestId, Photo())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendJsonAsync(context.Client, HttpMethod.Delete, path + "/photo",
            new { requestId = Guid.NewGuid(), expectedVersion = Version(old) })).StatusCode);

        // THEN the replacement remains current and both superseded URLs are inaccessible.
        await AssertUnchangedAsync(context.Client, path, current);
        await AssertHiddenAsync(context.Client, old);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentSessionsRaceReplacementAgainstMutationWithOneWinner(bool remove)
    {
        // GIVEN independent authenticated sessions acting on the same current version.
        await using var context = await TestContext.CreateAsync(sqlServer);
        var (path, old) = await context.CreatePhotographedItemAsync();
        using var second = context.Factory.CreateClient();
        await LoginAsync(second);
        context.Store.PauseNextPublication();

        // WHEN one upload pauses after publishing bytes and another mutation commits.
        var first = UploadAsync(context.Client, path, Version(old), Guid.NewGuid(), Photo(blue: true));
        try
        {
            await context.Store.Published.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await AssertUnchangedAsync(second, path, old);
            var winner = remove
                ? await SendJsonAsync(second, HttpMethod.Delete, path + "/photo",
                    new { requestId = Guid.NewGuid(), expectedVersion = Version(old) })
                : await UploadAsync(second, path, Version(old), Guid.NewGuid(), Photo());
            Assert.Equal(HttpStatusCode.OK, winner.StatusCode);
        }
        finally
        {
            context.Store.Release.TrySetResult();
        }

        // THEN the delayed operation loses without resurrecting the prior pair or erasing the winner.
        Assert.Equal(HttpStatusCode.Conflict, (await first).StatusCode);
        var current = await second.GetFromJsonAsync<JsonElement>(path);
        Assert.NotEqual(Version(old), Version(current));
        Assert.Equal("Safety sapphire", current.GetProperty("name").GetString());
        Assert.Equal("Tray A", current.GetProperty("location").GetString());
        if (remove)
        {
            Assert.Equal(JsonValueKind.Null, current.GetProperty("photo").ValueKind);
        }
        else
        {
            Assert.NotEqual(PhotoId(old), PhotoId(current));
            await AssertUnchangedAsync(second, path, current);
        }
        await AssertHiddenAsync(second, old);
    }

    [Theory]
    [InlineData("stage", 1, false)]
    [InlineData("stage", 2, false)]
    [InlineData("publish", 1, false)]
    [InlineData("publish", 2, false)]
    [InlineData("stage", 1, true)]
    [InlineData("publish", 2, true)]
    public async Task ProviderFailurePreservesCurrentPhotoAndExactRetryRecovers(string operation, int ordinal, bool afterWrite)
    {
        // GIVEN a current pair and a provider that fails once at a specific variant boundary.
        await using var context = await TestContext.CreateAsync(sqlServer);
        var (path, old) = await context.CreatePhotographedItemAsync();
        context.Store.FailOnce(operation, ordinal, afterWrite);
        var requestId = Guid.NewGuid();
        var replacement = Photo(blue: true);

        // WHEN storage fails, including an ambiguous acknowledgement after the write succeeded.
        var failed = await UploadAsync(context.Client, path, Version(old), requestId, replacement);
        if (afterWrite && operation == "stage" && failed.StatusCode == HttpStatusCode.OK)
        {
            // THEN an immediately verified acknowledgement is durable and replay stays idempotent.
            var recovered = await context.Client.GetFromJsonAsync<JsonElement>(path);
            Assert.NotEqual(PhotoId(old), PhotoId(recovered));
            Assert.Equal(HttpStatusCode.OK,
                (await UploadAsync(context.Client, path, Version(old), requestId, replacement)).StatusCode);
            await AssertUnchangedAsync(context.Client, path, recovered);
            await AssertHiddenAsync(context.Client, old);
            return;
        }
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        await AssertUnchangedAsync(context.Client, path, old);
        if (afterWrite)
        {
            Assert.True(context.Store.StoredFileCount > 2);
        }

        // THEN retrying the same operation recovers its pending objects and atomically replaces the pair.
        Assert.Equal(HttpStatusCode.OK,
            (await UploadAsync(context.Client, path, Version(old), requestId, replacement)).StatusCode);
        var current = await context.Client.GetFromJsonAsync<JsonElement>(path);
        Assert.NotEqual(PhotoId(old), PhotoId(current));
        await AssertUnchangedAsync(context.Client, path, current);
        await AssertHiddenAsync(context.Client, old);
    }

    [Fact]
    public async Task CorruptStoredBytesAreRejectedBeforeSuccessfulResponseStarts()
    {
        // GIVEN a committed pair whose provider subsequently returns corrupted bytes.
        await using var context = await TestContext.CreateAsync(sqlServer);
        var (path, current) = await context.CreatePhotographedItemAsync();
        context.Store.CorruptReads = true;

        // WHEN the owner asks for either variant.
        foreach (var variant in new[] { "detailUrl", "thumbnailUrl" })
        {
            var response = await context.Client.GetAsync(current.GetProperty("photo").GetProperty(variant).GetString());

            // THEN an integrity failure returns a retryable error instead of a successful image response.
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.NotEqual("image/webp", response.Content.Headers.ContentType?.MediaType);
        }
        context.Store.CorruptReads = false;
        await AssertUnchangedAsync(context.Client, path, current);
    }

    private static string Version(JsonElement item) => item.GetProperty("version").GetString()!;
    private static Guid PhotoId(JsonElement item) => item.GetProperty("photo").GetProperty("id").GetGuid();

    private static byte[] Photo(bool blue = false) => blue
        ? PhotoFixture.Png(red: 0, blue: 255)
        : PhotoFixture.Png();

    private static async Task AssertUnchangedAsync(HttpClient client, string path, JsonElement expected)
    {
        var current = await client.GetFromJsonAsync<JsonElement>(path);
        Assert.Equal(Version(expected), Version(current));
        Assert.Equal(PhotoId(expected), PhotoId(current));
        foreach (var variant in new[] { "detailUrl", "thumbnailUrl" })
        {
            var response = await client.GetAsync(current.GetProperty("photo").GetProperty(variant).GetString());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
        }
    }

    private static async Task AssertHiddenAsync(HttpClient client, JsonElement old)
    {
        foreach (var variant in new[] { "detailUrl", "thumbnailUrl" })
        {
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.GetAsync(old.GetProperty("photo").GetProperty(variant).GetString())).StatusCode);
        }
    }

    private sealed class TestContext(AuthTestApplication application, ControlledStore store,
        WebApplicationFactory<Program> factory, HttpClient client) : IAsyncDisposable
    {
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

        public async Task<(string Path, JsonElement Item)> CreateItemAsync()
        {
            var response = await SendJsonAsync(Client, HttpMethod.Post, "/api/items",
                new { creationRequestId = Guid.NewGuid(), name = "Safety sapphire", location = "Tray A" });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var path = response.Headers.Location!.ToString();
            return (path, await Client.GetFromJsonAsync<JsonElement>(path));
        }

        public async Task<(string Path, JsonElement Item)> CreatePhotographedItemAsync()
        {
            var (path, item) = await CreateItemAsync();
            Assert.Equal(HttpStatusCode.OK,
                (await UploadAsync(Client, path, Version(item), Guid.NewGuid(), Photo())).StatusCode);
            return (path, await Client.GetFromJsonAsync<JsonElement>(path));
        }

        public async ValueTask DisposeAsync()
        {
            Store.Release.TrySetResult();
            Client.Dispose();
            await Factory.DisposeAsync();
            await application.DisposeAsync();
            Store.Dispose();
        }
    }

    private sealed class ControlledStore : IBlobStore, IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "workbench-photo-safety-" + Guid.NewGuid().ToString("N"));
        private readonly FileSystemBlobStore _inner;
        private string? _failureOperation;
        private int _failureOrdinal;
        private bool _failureAfterWrite;
        private int _pausePublication;

        public ControlledStore()
        {
            Directory.CreateDirectory(_root);
            _inner = new FileSystemBlobStore(_root);
        }

        public string Alias => _inner.Alias;
        public bool CorruptReads { get; set; }
        public int StoredFileCount => Directory.GetFiles(_root).Length;
        public TaskCompletionSource Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void PauseNextPublication() => Interlocked.Exchange(ref _pausePublication, 1);

        public void FailOnce(string operation, int ordinal, bool afterWrite)
        {
            _failureOperation = operation;
            _failureOrdinal = ordinal;
            _failureAfterWrite = afterWrite;
        }

        private bool ShouldFail(string operation) =>
            _failureOperation == operation && Interlocked.Decrement(ref _failureOrdinal) == 0;

        public async Task<BlobContentIdentity> StageAsync(BlobObjectId id, Stream content, long maximumBytes, CancellationToken cancellationToken)
        {
            var fail = ShouldFail("stage");
            if (fail && !_failureAfterWrite)
            {
                throw new IOException("Injected storage stage failure.");
            }
            var result = await _inner.StageAsync(id, content, maximumBytes, cancellationToken);
            if (fail)
            {
                throw new IOException("Injected ambiguous storage stage acknowledgement.");
            }
            return result;
        }

        public async Task PublishAsync(BlobObjectId id, CancellationToken cancellationToken)
        {
            var fail = ShouldFail("publish");
            if (fail && !_failureAfterWrite)
            {
                throw new IOException("Injected storage publish failure.");
            }
            await _inner.PublishAsync(id, cancellationToken);
            if (fail)
            {
                throw new IOException("Injected ambiguous storage publication acknowledgement.");
            }
            if (Interlocked.Exchange(ref _pausePublication, 0) == 1)
            {
                Published.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
        }

        public async Task<Stream> OpenReadAsync(BlobObjectId id, CancellationToken cancellationToken)
        {
            var stream = await _inner.OpenReadAsync(id, cancellationToken);
            if (!CorruptReads)
            {
                return stream;
            }
            await using (stream)
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                var bytes = buffer.ToArray();
                bytes[^1] ^= 1;
                return new MemoryStream(bytes, writable: false);
            }
        }

        public Task DeleteAsync(BlobObjectId id, CancellationToken cancellationToken) => _inner.DeleteAsync(id, cancellationToken);
        public IAsyncEnumerable<BlobObjectId> ListAsync(CancellationToken cancellationToken) => _inner.ListAsync(cancellationToken);
        public Task CheckReadyAsync(CancellationToken cancellationToken) => _inner.CheckReadyAsync(cancellationToken);
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
