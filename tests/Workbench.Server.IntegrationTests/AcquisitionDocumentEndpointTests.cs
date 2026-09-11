// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Inventory;
using Workbench.Server.Storage;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class AcquisitionDocumentEndpointTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task DocumentsPersistReplayAndDownloadOnlyThroughOwnedContext()
    {
        // GIVEN a private acquisition and a durable test provider.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var storage = new DocumentTestStorage();
        await using var factory = application.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IBlobStore>();
            services.AddSingleton<IBlobStore>(storage.Store);
        }));
        using var client = factory.CreateClient();
        await LoginAsync(client);
        var item = await CreateItemAsync(client);
        var created = await SendAsync(client, HttpMethod.Post, $"/api/items/{item.Id}/acquisition",
            new CreateAcquisitionRequest(Guid.NewGuid(), item.Version, "Gift", null, null, null, null, null));
        var context = (await created.Content.ReadFromJsonAsync<ItemAcquisitionResponse>())!;
        var path = $"/api/items/{item.Id}/acquisition/{context.Acquisition!.Id}/documents";
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        var content = PhotoFixture.Png();
        var requestId = Guid.NewGuid();

        // WHEN an upload succeeds and its response is retried with the same immutable request.
        var uploaded = await UploadAsync(client, path, context, requestId, content);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var operation = (await uploaded.Content.ReadFromJsonAsync<AcquisitionDocumentOperationResponse>())!;
        Assert.Equal("Completed", operation.State);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(client, path, context, requestId, content)).StatusCode);

        // THEN a new session sees one labeled original file and safe, truthful delivery headers.
        using var later = factory.CreateClient();
        await LoginAsync(later);
        var listing = (await later.GetFromJsonAsync<AcquisitionDocumentsResponse>(path))!;
        var document = Assert.Single(listing.Documents);
        Assert.Equal("Receipt", document.Label);
        Assert.Equal(content.Length, document.Length);
        Assert.Equal("image/png", document.MediaType);
        var downloadPath = $"{path}/{document.Id}/download";
        var download = await later.GetAsync(downloadPath);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(content, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(content.Length, download.Content.Headers.ContentLength);
        Assert.Equal("nosniff", Assert.Single(download.Headers.GetValues("X-Content-Type-Options")));
        Assert.True(download.Headers.CacheControl?.NoStore);
        Assert.True(download.Headers.CacheControl?.Private);

        // AND direct foreign identifiers disclose neither metadata, command evidence nor content.
        using var other = factory.CreateClient();
        await LoginAsync(other, "other@example.com");
        foreach (var target in new[] { path, downloadPath, $"{path}/operations/{requestId}" })
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(target)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(other, path, context, Guid.NewGuid(), content)).StatusCode);
        var foreignChange = new ChangeAcquisitionDocumentRequest(Guid.NewGuid(), listing.ItemVersion,
            listing.AcquisitionVersion, document.Version, "Foreign rename");
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(other, HttpMethod.Put, $"{path}/{document.Id}", foreignChange)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(other, HttpMethod.Delete, $"{path}/{document.Id}", foreignChange with { Label = null })).StatusCode);

        // WHEN the item is archived THEN documents remain readable but fresh mutations are rejected.
        var archived = await SendAsync(later, HttpMethod.Post, $"/api/items/{item.Id}/archive", new { expectedVersion = listing.ItemVersion });
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await later.GetAsync(downloadPath)).StatusCode);
        var currentItem = (await archived.Content.ReadFromJsonAsync<ItemDetailResponse>())!;
        var archivedContext = context with
        {
            ItemVersion = currentItem.Version,
            Acquisition = context.Acquisition with { Version = listing.AcquisitionVersion }
        };
        Assert.Equal(HttpStatusCode.Conflict, (await UploadAsync(later, path, archivedContext, Guid.NewGuid(), content)).StatusCode);
        // AND restoration keeps the same stored document accessible.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(later, HttpMethod.Post, $"/api/items/{item.Id}/restore",
            new { expectedVersion = currentItem.Version })).StatusCode);
        Assert.Equal(content, await later.GetByteArrayAsync(downloadPath));
    }

    [Fact]
    public async Task RejectionCorrectionAndRemovalNeverOverwriteOrDuplicateDocuments()
    {
        // GIVEN a saved acquisition with a validated receipt.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var storage = new DocumentTestStorage();
        await using var factory = application.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IBlobStore>();
            services.AddSingleton<IBlobStore>(storage.Store);
        }));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/items/{Guid.NewGuid()}/acquisition/{Guid.NewGuid()}/documents")).StatusCode);
        await LoginAsync(client);
        var item = await CreateItemAsync(client);
        var created = await SendAsync(client, HttpMethod.Post, $"/api/items/{item.Id}/acquisition",
            new CreateAcquisitionRequest(Guid.NewGuid(), item.Version, "Unknown", null, null, null, null, null));
        var context = (await created.Content.ReadFromJsonAsync<ItemAcquisitionResponse>())!;
        var path = $"/api/items/{item.Id}/acquisition/{context.Acquisition!.Id}/documents";
        var bytes = PhotoFixture.Png();
        var requestId = Guid.NewGuid();

        // AND uploads without antiforgery proof cannot change saved evidence.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path, new MultipartFormDataContent())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(client, path, context, requestId, bytes)).StatusCode);
        var list = (await client.GetFromJsonAsync<AcquisitionDocumentsResponse>(path))!;
        var document = Assert.Single(list.Documents);
        var current = context with { ItemVersion = list.ItemVersion, Acquisition = context.Acquisition with { Version = list.AcquisitionVersion } };

        // WHEN unsupported, oversized and mislabeled uploads are attempted.
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await UploadAsync(client, path, current, Guid.NewGuid(), "<html>unsafe</html>"u8.ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await UploadAsync(client, path, current, Guid.NewGuid(), new byte[10 * 1024 * 1024 + 1])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(client, path, current, Guid.NewGuid(), bytes, "  ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(client, path, current, Guid.NewGuid(), bytes, new string('x', 201))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await UploadAsync(client, path, context, requestId, bytes, "Different request payload")).StatusCode);

        // THEN the original file and its metadata remain intact.
        Assert.Equal(document, Assert.Single((await client.GetFromJsonAsync<AcquisitionDocumentsResponse>(path))!.Documents));
        Assert.Equal(bytes, await client.GetByteArrayAsync($"{path}/{document.Id}/download"));

        // WHEN a label correction is submitted and replayed THEN it happens once and stale writes fail.
        var rename = new ChangeAcquisitionDocumentRequest(Guid.NewGuid(), list.ItemVersion, list.AcquisitionVersion, document.Version, "Corrected receipt");
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, $"{path}/{document.Id}", rename)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, $"{path}/{document.Id}", rename)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Put, $"{path}/{document.Id}", rename with { RequestId = Guid.NewGuid(), Label = "Stale" })).StatusCode);
        list = (await client.GetFromJsonAsync<AcquisitionDocumentsResponse>(path))!;
        document = Assert.Single(list.Documents);
        Assert.Equal("Corrected receipt", document.Label);

        // WHEN explicit removal succeeds and replays THEN the old URL stops serving and upload replay cannot resurrect it.
        var remove = new ChangeAcquisitionDocumentRequest(Guid.NewGuid(), list.ItemVersion, list.AcquisitionVersion, document.Version, null);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Delete, $"{path}/{document.Id}", remove)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Delete, $"{path}/{document.Id}", remove)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(client, path, context, requestId, bytes)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<AcquisitionDocumentsResponse>(path))!.Documents);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{path}/{document.Id}/download")).StatusCode);
    }

    private sealed class DocumentTestStorage : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "workbench-document-test-" + Guid.NewGuid().ToString("N"));
        public FileSystemBlobStore Store { get; }
        public DocumentTestStorage()
        {
            Directory.CreateDirectory(root);
            Store = new FileSystemBlobStore(root);
        }
        public void Dispose() => Directory.Delete(root, recursive: true);
    }

    internal static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string path, ItemAcquisitionResponse context,
        Guid requestId, byte[] bytes, string label = "Receipt")
    {
        var token = await client.GetFromJsonAsync<JsonElement>("/api/auth/antiforgery");
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-TOKEN", token.GetProperty("requestToken").GetString());
        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(requestId.ToString()), "requestId");
        multipart.Add(new StringContent(context.ItemVersion), "expectedItemVersion");
        multipart.Add(new StringContent(context.Acquisition!.Version), "expectedAcquisitionVersion");
        multipart.Add(new StringContent(label), "label");
        multipart.Add(new ByteArrayContent(bytes), "file", "untrusted-name.pdf");
        request.Content = multipart;
        return await client.SendAsync(request);
    }
}
