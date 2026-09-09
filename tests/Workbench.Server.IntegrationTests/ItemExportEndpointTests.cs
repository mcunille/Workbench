// Copyright (c) 2026 The White Stag Collection.

using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Storage;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ItemExportEndpointTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportRequiresExplicitScopeAndReturnsEmptyWithoutAFile(bool package)
    {
        // GIVEN an authenticated member with no items.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        await using var storageFactory = CreateExportFactory(app);
        using var client = storageFactory.CreateClient();
        await LoginAsync(client);
        // WHEN exporting without a scope, with an invalid scope, or an empty valid scope.
        var missing = await PostAsync(client, ExportPath(package), new { });
        var invalid = await PostAsync(client, ExportPath(package), new { scope = "archived" });
        var empty = await PostAsync(client, ExportPath(package), new { scope = "active" });
        // THEN intent is required and an empty export never produces an attachment.
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, empty.StatusCode);
        Assert.Null(empty.Content.Headers.ContentDisposition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportIncludesAllPagesAndExplicitArchiveScopeButNeverAnotherTenant(bool package)
    {
        // GIVEN more than one page of records, including an archived item and a foreign item.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        await using var storageFactory = CreateExportFactory(app);
        using var client = storageFactory.CreateClient();
        using var other = storageFactory.CreateClient();
        await LoginAsync(client);
        await LoginAsync(other, "other@example.com");
        var ids = new List<string>();
        for (var index = 0; index < 51; index++)
        {
            var created = await PostAsync(client, "/api/items", new { creationRequestId = Guid.NewGuid(), name = $"Stone {index}" });
            var item = await created.Content.ReadFromJsonAsync<JsonElement>();
            ids.Add(item.GetProperty("id").GetString()!);
            if (index == 0)
                Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, $"/api/items/{ids[0]}/archive", new { expectedVersion = item.GetProperty("version").GetString() })).StatusCode);
        }
        var foreignResponse = await PostAsync(other, "/api/items", new { creationRequestId = Guid.NewGuid(), name = "Foreign secret" });
        var foreign = await foreignResponse.Content.ReadFromJsonAsync<JsonElement>();
        var foreignId = foreign.GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(other, $"/api/items/{foreignId}/archive",
            new { expectedVersion = foreign.GetProperty("version").GetString() })).StatusCode);
        // WHEN exporting each explicit scope without pagination or search parameters.
        var active = await PostAsync(client, ExportPath(package), new { scope = "active" });
        var all = await PostAsync(client, ExportPath(package), new { scope = "all" });
        // THEN all selected rows and only this tenant are included with a complete private attachment.
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        var activeCsv = await ReadRecordsAsync(active, package);
        var allCsv = await ReadRecordsAsync(all, package);
        Assert.DoesNotContain(ids[0], activeCsv);
        Assert.All(ids, id => Assert.Contains(id, allCsv));
        Assert.DoesNotContain("Foreign secret", allCsv);
        Assert.DoesNotContain(foreignId, allCsv);
        Assert.Equal(52, allCsv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(package ? "application/zip" : "text/csv", all.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", all.Content.Headers.ContentDisposition?.DispositionType);
        Assert.True(all.Headers.CacheControl?.NoStore);
        var bytes = await all.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes.Length, all.Content.Headers.ContentLength);
        if (package)
        {
            using var zip = new ZipArchive(new MemoryStream(bytes));
            using var csv = zip.GetEntry("records.csv")!.Open();
            var prefix = new byte[3];
            await csv.ReadExactlyAsync(prefix);
            Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, prefix);
            using var manifest = JsonDocument.Parse(await ItemPackageEndpointTests.ReadAsync(zip, "manifest.json"));
            var manifestIds = manifest.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("item_id").GetString()).ToArray();
            Assert.Equal(ids.Order(), manifestIds.Order());
            Assert.DoesNotContain(foreignId, manifestIds);
        }
        else Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, bytes[..3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportRequiresAuthenticationAndAntiforgery(bool package)
    {
        // GIVEN an anonymous client and then a member lacking the antiforgery header.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        await using var storageFactory = CreateExportFactory(app);
        using var client = storageFactory.CreateClient();
        // WHEN requesting a private export.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(ExportPath(package), new { scope = "all" })).StatusCode);
        await LoginAsync(client);
        // THEN authentication alone does not bypass antiforgery validation.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(ExportPath(package), new { scope = "all" })).StatusCode);
    }

    internal static string ExportPath(bool package) => package ? "/api/items/export-package" : "/api/items/export";

    internal static WebApplicationFactory<Program> CreateExportFactory(AuthTestApplication application) =>
        application.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            // These text-only fixtures require provider registration but never access a physical blob.
            services.RemoveAll<IBlobStore>();
            services.AddSingleton<IBlobStore>(new FileSystemBlobStore(
                Path.Combine(Path.GetTempPath(), "workbench-export-empty-" + Guid.NewGuid().ToString("N"))));
        }));

    internal static async Task<string> ReadRecordsAsync(HttpResponseMessage response, bool package)
    {
        if (!package || response.StatusCode == HttpStatusCode.NoContent) return await response.Content.ReadAsStringAsync();
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        return await ItemPackageEndpointTests.ReadAsync(zip, "records.csv");
    }

    internal static async Task LoginAsync(HttpClient client, string email = "member@example.com") =>
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(client, "/api/auth/login", new { email, password = AuthTestApplication.AdminPassword })).StatusCode);

    internal static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body, CancellationToken cancellationToken = default)
    {
        var token = await client.GetFromJsonAsync<JsonElement>("/api/auth/antiforgery");
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", token.GetProperty("requestToken").GetString());
        return await client.SendAsync(request, cancellationToken);
    }
}
