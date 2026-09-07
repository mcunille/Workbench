// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Inventory;
using Workbench.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ItemArchivingTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task ArchivedRowsAreExcludedBeforeSearchPagination()
    {
        // GIVEN 56 matching records, with the earliest five archived through the checked command.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        await using var sql = new SqlConnection(app.AdminConnectionString);
        await sql.OpenAsync();
        await using var seed = new SqlCommand("""
            DECLARE @n int = 0, @id uniqueidentifier, @version binary(8);
            WHILE @n < 56
            BEGIN
                SET @id=NEWID();
                INSERT [Inventory].[Items] (Id,TenantId,TrackingKind,Name,CreatedAtUtc,CreationRequestId)
                    VALUES(@id,@tenant,'Individual',N'Pagination specimen',DATEADD(second,@n,'2026-01-01'),NEWID());
                IF @n < 5
                BEGIN
                    SELECT @version=RowVersion FROM [Inventory].[Items] WHERE Id=@id;
                    BEGIN TRANSACTION;
                    EXEC [Inventory].[ArchiveItem] @Id=@id,@ExpectedVersion=@version;
                    COMMIT;
                END;
                SET @n=@n+1;
            END;
            """, sql);
        seed.Parameters.AddWithValue("@tenant", AuthTestApplication.TenantId);
        await seed.ExecuteNonQueryAsync();
        using var client = app.CreateClient();
        await LoginAsync(client, "member@example.com");
        // WHEN ordinary and searched collection pages are traversed.
        foreach (var query in new[] { "", "q=specimen&" })
        {
            var first = (await client.GetFromJsonAsync<ItemPageResponse>("/api/items?" + query))!;
            // THEN the full first page and final page contain exactly the 51 active identities.
            Assert.Equal(50, first.Items.Count);
            Assert.NotNull(first.NextCursor);
            var second = (await client.GetFromJsonAsync<ItemPageResponse>("/api/items?" + query + "cursor=" + Uri.EscapeDataString(first.NextCursor)))!;
            Assert.Single(second.Items);
            Assert.Null(second.NextCursor);
            Assert.Equal(51, first.Items.Concat(second.Items).Select(row => row.Id).Distinct().Count());
            Assert.All(first.Items.Concat(second.Items), row => Assert.True(row.CreatedAtUtc.Second >= 5));
        }
    }

    [Fact]
    public async Task ArchivePreservesIdentityReplayAndDirectReadButRemovesSearchAndRejectsWrites()
    {
        // GIVEN a collector's saved item and its original creation request.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var storage = new PhotoStorage();
        await using var factory = app.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IBlobStore>();
            services.AddSingleton<IBlobStore>(storage.Store);
        }));
        using var client = factory.CreateClient();
        await LoginAsync(client, "member@example.com");
        var creation = new { creationRequestId = Guid.NewGuid(), name = "Sapphire", notes = "Original", location = "Tray" };
        var item = (await (await SendAsync(client, HttpMethod.Post, "/api/items", creation)).Content.ReadFromJsonAsync<ItemDetailResponse>())!;
        var photoRequest = Guid.NewGuid();
        var photoVersion = item.Version;
        var uploaded = await ItemPhotoEndpointTests.UploadAsync(client, $"/api/items/{item.Id}", photoVersion, photoRequest);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        item = (await client.GetFromJsonAsync<ItemDetailResponse>($"/api/items/{item.Id}"))!;
        // WHEN archiving with the displayed version.
        var response = await SendAsync(client, HttpMethod.Post, $"/api/items/{item.Id}/archive", new { expectedVersion = item.Version });
        // THEN the commit retains the identity and details while advancing the version.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.String, json.GetProperty("archivedAtUtc").ValueKind);
        var archived = (await client.GetFromJsonAsync<ItemDetailResponse>($"/api/items/{item.Id}"))!;
        Assert.Equal(item.Id, archived.Id);
        Assert.Equal(item.Name, archived.Name);
        Assert.Equal(item.Notes, archived.Notes);
        Assert.Equal(item.Location, archived.Location);
        Assert.Equal(item.CreatedAtUtc, archived.CreatedAtUtc);
        Assert.NotEqual(item.Version, archived.Version);
        Assert.Equal(item.Photo, archived.Photo);
        // AND retained photo bytes and completed photo replay stay available without another write.
        foreach (var url in new[] { item.Photo!.DetailUrl, item.Photo.ThumbnailUrl })
        {
            var photo = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
            Assert.NotEmpty(await photo.Content.ReadAsByteArrayAsync());
        }
        var replayPhoto = await ItemPhotoEndpointTests.UploadAsync(client, $"/api/items/{item.Id}", photoVersion, photoRequest);
        Assert.Equal(HttpStatusCode.OK, replayPhoto.StatusCode);
        Assert.Equal(await uploaded.Content.ReadAsStringAsync(), await replayPhoto.Content.ReadAsStringAsync());
        using var foreign = factory.CreateClient();
        await LoginAsync(foreign, "other@example.com");
        foreach (var url in new[] { $"/api/items/{item.Id}", item.Photo.DetailUrl, item.Photo.ThumbnailUrl })
            Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync(url)).StatusCode);
        // AND browsing/search exclude it, while original creation replay returns the same record.
        foreach (var path in new[] { "/api/items", "/api/items?q=Sapphire" })
            Assert.Empty((await client.GetFromJsonAsync<ItemPageResponse>(path))!.Items);
        Assert.Equal(archived, await (await SendAsync(client, HttpMethod.Post, "/api/items", creation)).Content.ReadFromJsonAsync<ItemDetailResponse>());
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Post, "/api/items", new { creation.creationRequestId, name = "Different" })).StatusCode);
        // AND old retries and new mutations with even the archived version cannot change it.
        foreach (var version in new[] { item.Version, archived.Version })
        {
            var retry = await SendAsync(client, HttpMethod.Post, $"/api/items/{item.Id}/archive", new { expectedVersion = version });
            Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
            Assert.Equal("item_archived", (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Put, $"/api/items/{item.Id}", new { expectedVersion = version, name = "Changed" })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Delete, $"/api/items/{item.Id}/photo", new { expectedVersion = version, requestId = Guid.NewGuid() })).StatusCode);
        }
        Assert.Equal(archived, await client.GetFromJsonAsync<ItemDetailResponse>($"/api/items/{item.Id}"));
    }

    [Fact]
    public async Task ArchiveChecksValidationAntiforgeryTenantAndStaleVersions()
    {
        // GIVEN an active record and independent tenant sessions.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        using var other = app.CreateClient();
        using var anonymous = app.CreateClient();
        await LoginAsync(client, "member@example.com");
        await LoginAsync(other, "other@example.com");
        var item = (await (await SendAsync(client, HttpMethod.Post, "/api/items", new { creationRequestId = Guid.NewGuid(), name = "Original" })).Content.ReadFromJsonAsync<ItemDetailResponse>())!;
        var path = $"/api/items/{item.Id}/archive";
        // WHEN malformed, unprotected, or unauthorized requests attempt archival.
        foreach (var body in new object[] { new { }, new { expectedVersion = "bad" }, new { expectedVersion = "AQ==" }, new { expectedVersion = item.Version, archivedAtUtc = DateTimeOffset.UtcNow } })
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(client, HttpMethod.Post, path, body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { expectedVersion = item.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, HttpMethod.Post, path, new { expectedVersion = item.Version })).StatusCode);
        foreach (var id in new[] { item.Id, Guid.NewGuid() })
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(other, HttpMethod.Post, $"/api/items/{id}/archive", new { expectedVersion = item.Version })).StatusCode);
        // AND a newer edit commits before archive is confirmed.
        var edit = await SendAsync(client, HttpMethod.Put, $"/api/items/{item.Id}", new { expectedVersion = item.Version, name = "Newer" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var conflict = await SendAsync(client, HttpMethod.Post, path, new { expectedVersion = item.Version });
        // THEN the stale request cannot archive or overwrite the newer record.
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("item_version_conflict", (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal("Newer", (await client.GetFromJsonAsync<ItemDetailResponse>($"/api/items/{item.Id}"))!.Name);
        Assert.Single((await client.GetFromJsonAsync<ItemPageResponse>("/api/items"))!.Items);
    }

    private static async Task LoginAsync(HttpClient client, string email) =>
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, HttpMethod.Post, "/api/auth/login", new { email, password = AuthTestApplication.AdminPassword })).StatusCode);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object body)
    {
        var token = await client.GetFromJsonAsync<JsonElement>("/api/auth/antiforgery");
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", token.GetProperty("requestToken").GetString());
        return await client.SendAsync(request);
    }

    private sealed class PhotoStorage : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "workbench-archive-photo-" + Guid.NewGuid().ToString("N"));
        public FileSystemBlobStore Store { get; }
        public PhotoStorage()
        {
            Directory.CreateDirectory(root);
            Store = new FileSystemBlobStore(root);
        }
        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
