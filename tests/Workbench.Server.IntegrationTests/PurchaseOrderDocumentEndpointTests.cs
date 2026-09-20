// Copyright (c) 2026 The White Stag Collection.
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Server.Storage;
using System.Net.Http.Json;
using System.Text.Json;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Purchasing;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;
namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class PurchaseOrderDocumentEndpointTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task OrderedPurchaseExposesPrivateEmptyDocumentCollection()
    {
        // GIVEN an authenticated owner with a saved draft.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var storage = new TestStorage();
        await using var factory = app.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IBlobStore>(); services.AddSingleton<IBlobStore>(storage.Store); }));
        using var client = factory.CreateClient(); await LoginAsync(client);
        var draft = DraftOrderPricingTests.Empty with { SupplierName = "Supplier", Entries = [DraftOrderPricingTests.Line with { Description = "Sapphire" }] };
        var created = await SendAsync(client, HttpMethod.Post, "/api/beta/purchase-order-drafts", new CreateDraftOrderRequest(Guid.NewGuid(), draft));
        var saved = (await created.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        var path = $"/api/beta/purchase-orders/{saved.DraftOrderId}/documents";
        // WHEN documents are requested before commitment THEN draft planning does not accept paperwork.
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(path)).StatusCode);
        await SendAsync(client, HttpMethod.Post, $"/api/beta/purchase-order-drafts/{saved.DraftOrderId}/commit", new { requestId = Guid.NewGuid(), expectedVersion = saved.SavedVersion, orderDate = "2026-09-18" });
        // WHEN the owner opens ordered purchase documents THEN the collection and current order version are returned privately.
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("documents").EnumerateArray());
        Assert.Equal(8, Convert.FromBase64String(body.GetProperty("orderVersion").GetString()!).Length);
        Assert.True(response.Headers.CacheControl?.NoStore);
        // AND another tenant cannot discover its documents.
        using var other = factory.CreateClient(); await LoginAsync(other, "other@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(path)).StatusCode);
    }
    [Fact]
    public async Task RejectionCorrectionAndRemovalNeverOverwriteOrDuplicateDocuments()
    {
        // GIVEN an ordered purchase with a validated receipt.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var storage = new TestStorage();
        await using var factory = application.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IBlobStore>();
            services.AddSingleton<IBlobStore>(storage.Store);
        }));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/beta/purchase-orders/{Guid.NewGuid()}/documents")).StatusCode);
        await LoginAsync(client);
        var context = await CreateOrderedAsync(client);
        var path = $"/api/beta/purchase-orders/{context.DraftOrderId}/documents";
        var bytes = DocumentValidatorTests.Pdf();
        var requestId = Guid.NewGuid();

        // AND uploads without antiforgery proof cannot change saved evidence.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path, new MultipartFormDataContent())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(client, path, context.SavedVersion, requestId, bytes)).StatusCode);
        var list = (await client.GetFromJsonAsync<PurchaseOrderDocumentsResponse>(path))!;
        var document = Assert.Single(list.Documents);
        var current = context with { SavedVersion = list.OrderVersion };

        // WHEN unsupported, oversized and mislabeled uploads are attempted.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await UploadAsync(client, path, current.SavedVersion, Guid.NewGuid(), "%PDF-1.7\n"u8.ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await UploadAsync(client, path, current.SavedVersion, Guid.NewGuid(), "<html>unsafe</html>"u8.ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await UploadAsync(client, path, current.SavedVersion, Guid.NewGuid(), new byte[10 * 1024 * 1024 + 1])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(client, path, current.SavedVersion, Guid.NewGuid(), bytes, "  ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(client, path, current.SavedVersion, Guid.NewGuid(), bytes, new string('x', 201))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await UploadAsync(client, path, context.SavedVersion, requestId, bytes, "Different request payload")).StatusCode);

        // THEN the original file and its metadata remain intact.
        Assert.Equal(document, Assert.Single((await client.GetFromJsonAsync<PurchaseOrderDocumentsResponse>(path))!.Documents));
        Assert.Equal(bytes, await client.GetByteArrayAsync($"{path}/{document.Id}/download"));

        // WHEN a label correction is submitted and replayed THEN it happens once and stale writes fail.
        var rename = new ChangePurchaseOrderDocumentRequest(Guid.NewGuid(), list.OrderVersion, document.Version, "Corrected invoice");
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, $"{path}/{document.Id}", rename)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, $"{path}/{document.Id}", rename)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Put, $"{path}/{document.Id}", rename with { RequestId = Guid.NewGuid(), Label = "Stale" })).StatusCode);
        list = (await client.GetFromJsonAsync<PurchaseOrderDocumentsResponse>(path))!;
        document = Assert.Single(list.Documents);
        Assert.Equal("Corrected invoice", document.Label);

        // AND ordinary downloads stay private and serve the immutable validated bytes.
        var download = await client.GetAsync($"{path}/{document.Id}/download");
        Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("nosniff", Assert.Single(download.Headers.GetValues("X-Content-Type-Options")));
        Assert.True(download.Headers.CacheControl?.NoStore);
        using var other = factory.CreateClient(); await LoginAsync(other, "other@example.com");
        foreach (var target in new[] { path, $"{path}/{document.Id}/download", $"{path}/operations/{requestId}" })
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(target)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(other, path, list.OrderVersion, Guid.NewGuid(), bytes)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(other, HttpMethod.Put, $"{path}/{document.Id}", rename)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(other, HttpMethod.Delete, $"{path}/{document.Id}", rename with { Label = null })).StatusCode);
        // WHEN explicit removal succeeds and replays THEN the old URL stops serving and upload replay cannot resurrect it.
        var remove = new ChangePurchaseOrderDocumentRequest(Guid.NewGuid(), list.OrderVersion, document.Version, null);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Delete, $"{path}/{document.Id}", remove)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Delete, $"{path}/{document.Id}", remove)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(client, path, context.SavedVersion, requestId, bytes)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<PurchaseOrderDocumentsResponse>(path))!.Documents);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{path}/{document.Id}/download")).StatusCode);
    }

    internal sealed class TestStorage : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "workbench-po-documents-" + Guid.NewGuid().ToString("N"));
        internal FileSystemBlobStore Store { get; }
        internal TestStorage() { Directory.CreateDirectory(root); Store = new(root); }
        public void Dispose() => Directory.Delete(root, true);
    }
    internal static async Task<SaveDraftOrderResponse> CreateOrderedAsync(HttpClient client)
    {
        var draft = DraftOrderPricingTests.Empty with { SupplierName = "Supplier", Entries = [DraftOrderPricingTests.Line with { Description = "Sapphire" }] };
        var created = await SendAsync(client, HttpMethod.Post, "/api/beta/purchase-order-drafts", new CreateDraftOrderRequest(Guid.NewGuid(), draft));
        var saved = (await created.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        var committed = await SendAsync(client, HttpMethod.Post, $"/api/beta/purchase-order-drafts/{saved.DraftOrderId}/commit", new { requestId = Guid.NewGuid(), expectedVersion = saved.SavedVersion, orderDate = "2026-09-18" });
        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        var result = await committed.Content.ReadFromJsonAsync<JsonElement>();
        return saved with { SavedVersion = result.GetProperty("savedVersion").GetString()! };
    }
    internal static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string path, string version, Guid requestId, byte[] bytes, string label = "Invoice")
    {
        var token = await client.GetFromJsonAsync<JsonElement>("/api/beta/auth/antiforgery");
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-CSRF-TOKEN", token.GetProperty("requestToken").GetString());
        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(requestId.ToString()), "requestId");
        multipart.Add(new StringContent(version), "expectedOrderVersion");
        multipart.Add(new StringContent(label), "label");
        multipart.Add(new ByteArrayContent(bytes), "file", "untrusted-name.pdf");
        request.Content = multipart;
        return await client.SendAsync(request);
    }
}
