// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Storage;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ItemPhotoEndpointTests(SqlServerFixture sqlServer)
{
    // A freshly encoded small PNG: API tests exercise HTTP/storage, decoder fixtures live separately.
    private static readonly byte[] Photo = PhotoFixture.Png();

    [Fact]
    public async Task UploadIsDurableAcrossSessionsAndRemovePreservesTheItem()
    {
        // GIVEN an owned saved item and a private filesystem provider.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var storage = new PhotoTestStorage();
        await using var factory = application.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IBlobStore>();
            services.AddSingleton<IBlobStore>(storage.Store);
        }));
        using var client = factory.CreateClient();
        await LoginAsync(client);
        var created = await SendJsonAsync(client, HttpMethod.Post, "/api/items", new { creationRequestId = Guid.NewGuid(), name = "Sapphire", location = "Tray A" });
        var path = created.Headers.Location!.ToString();
        var item = await created.Content.ReadFromJsonAsync<JsonElement>();
        var version = await ReadVersionAsync(application, item.GetProperty("id").GetGuid());

        // WHEN a prepared photograph is uploaded and its exact operation is retried.
        var requestId = Guid.NewGuid();
        var uploaded = await UploadAsync(client, path, version, requestId);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(client, path, version, requestId)).StatusCode);

        // THEN a new authorized session sees the same complete pair and intact item text.
        using var next = factory.CreateClient();
        await LoginAsync(next);
        var current = await next.GetFromJsonAsync<JsonElement>(path);
        Assert.Equal("Sapphire", current.GetProperty("name").GetString());
        Assert.Equal("Tray A", current.GetProperty("location").GetString());
        var photo = current.GetProperty("photo");
        var detailUrl = photo.GetProperty("detailUrl").GetString()!;
        foreach (var url in new[] { detailUrl, photo.GetProperty("thumbnailUrl").GetString()! })
        {
            var response = await next.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.True(response.Headers.CacheControl?.Private);
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
        }
        var list = await next.GetFromJsonAsync<JsonElement>("/api/items");
        Assert.Equal(photo.GetProperty("id").GetGuid(), list.GetProperty("items")[0].GetProperty("photo").GetProperty("id").GetGuid());

        // WHEN removing with the current version THEN delivery stops but the item stays saved.
        var remove = new { requestId = Guid.NewGuid(), expectedVersion = current.GetProperty("version").GetString() };
        Assert.Equal(HttpStatusCode.OK, (await SendJsonAsync(next, HttpMethod.Delete, path + "/photo", remove)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendJsonAsync(next, HttpMethod.Delete, path + "/photo", remove)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await next.GetAsync(detailUrl)).StatusCode);
        current = await next.GetFromJsonAsync<JsonElement>(path);
        Assert.Equal(JsonValueKind.Null, current.GetProperty("photo").ValueKind);
        Assert.Equal("Sapphire", current.GetProperty("name").GetString());
        await using var sql = new SqlConnection(application.AdminConnectionString);
        await sql.OpenAsync();
        await using var count = new SqlCommand("SELECT COUNT(*) FROM [Operations].[WorkItems] WHERE [Kind]=1 AND [AvailableAtUtc] > DATEADD(day, 6, SYSUTCDATETIME())", sql);
        Assert.Equal(2, Convert.ToInt32(await count.ExecuteScalarAsync()));
    }

    internal static async Task LoginAsync(HttpClient client, string email = "member@example.com") =>
        Assert.Equal(HttpStatusCode.NoContent, (await SendJsonAsync(client, HttpMethod.Post, "/api/auth/login",
            new { email, password = AuthTestApplication.AdminPassword })).StatusCode);

    internal static async Task<HttpResponseMessage> SendJsonAsync(HttpClient client, HttpMethod method, string path, object body)
    {
        var token = await client.GetFromJsonAsync<JsonElement>("/api/auth/antiforgery");
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", token.GetProperty("requestToken").GetString());
        return await client.SendAsync(request);
    }

    internal static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string path, string version, Guid requestId, byte[]? bytes = null)
    {
        var token = await client.GetFromJsonAsync<JsonElement>("/api/auth/antiforgery");
        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(bytes ?? Photo), "file", "prepared.png");
        body.Add(new StringContent(requestId.ToString()), "requestId");
        body.Add(new StringContent(version), "expectedVersion");
        using var request = new HttpRequestMessage(HttpMethod.Put, path + "/photo") { Content = body };
        request.Headers.Add("X-CSRF-TOKEN", token.GetProperty("requestToken").GetString());
        return await client.SendAsync(request);
    }

    private static async Task<string> ReadVersionAsync(AuthTestApplication application, Guid id)
    {
        await using var connection = new SqlConnection(application.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT [RowVersion] FROM [Inventory].[Items] WHERE [Id]=@id", connection);
        command.Parameters.AddWithValue("@id", id);
        return Convert.ToBase64String((byte[])(await command.ExecuteScalarAsync())!);
    }

    private sealed class PhotoTestStorage : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "workbench-photo-test-" + Guid.NewGuid().ToString("N"));
        public FileSystemBlobStore Store { get; }
        public PhotoTestStorage()
        {
            Directory.CreateDirectory(_root);
            Store = new FileSystemBlobStore(_root);
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
