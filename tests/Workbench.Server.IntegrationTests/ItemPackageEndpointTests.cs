// Copyright (c) 2026 The White Stag Collection.

using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Storage;
using Xunit;
using static Workbench.Server.IntegrationTests.ItemExportEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ItemPackageEndpointTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task PackageHasOfflineMappingAndExplicitAbsenceWithoutForeignRecords()
    {
        // GIVEN owned text containing Unicode, formula text and path separators, and a foreign record.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        await using var factory = app.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IBlobStore>();
            services.AddSingleton<IBlobStore>(new FileSystemBlobStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        }));
        using var client = factory.CreateClient();
        using var other = factory.CreateClient();
        await LoginAsync(client);
        await LoginAsync(other, "other@example.com");
        const string name = "=蓝../stone";
        var created = await PostAsync(client, "/api/items", new { creationRequestId = Guid.NewGuid(), name, location = "Tray A" });
        var item = await created.Content.ReadFromJsonAsync<JsonElement>();
        await PostAsync(other, "/api/items", new { creationRequestId = Guid.NewGuid(), name = "Foreign secret" });
        // WHEN preparing the complete package.
        var response = await PostAsync(client, "/api/items/export-package", new { scope = "all" });
        // THEN the ZIP is private, complete and independently readable with explicit photo absence.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.StartsWith("workbench-package-v1-all-", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Equal(new[] { "README.txt", "manifest.json", "records.csv" }, zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
        using var manifest = JsonDocument.Parse(await ReadAsync(zip, "manifest.json"));
        var record = Assert.Single(manifest.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(item.GetProperty("id").GetGuid(), record.GetProperty("item_id").GetGuid());
        Assert.Equal(name, record.GetProperty("name").GetString());
        Assert.Equal("Tray A", record.GetProperty("location").GetString());
        Assert.Equal("none", record.GetProperty("photo").GetProperty("status").GetString());
        Assert.Contains("'" + name, await ReadAsync(zip, "records.csv"));
        Assert.DoesNotContain("Foreign secret", await ReadAsync(zip, "records.csv"));
        Assert.Contains("apostrophe", await ReadAsync(zip, "README.txt"));
    }

    [Fact]
    public async Task PackageRequiresAuthenticationAntiforgeryAndExplicitScope()
    {
        // GIVEN an anonymous client, then an authenticated member with an empty collection.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        await using var factory = app.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IBlobStore>();
            services.AddSingleton<IBlobStore>(new FileSystemBlobStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        }));
        using var client = factory.CreateClient();
        // WHEN attempting the package with missing authority or invalid intent.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/items/export-package", new { scope = "all" })).StatusCode);
        await LoginAsync(client);
        // THEN ordinary authentication, antiforgery and validation contracts apply; empty is not a ZIP.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/items/export-package", new { scope = "all" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, "/api/items/export-package", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, "/api/items/export-package", new { scope = "archived" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(client, "/api/items/export-package", new { scope = "all" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/items/export-package/{Guid.NewGuid()}")).StatusCode);
    }

    [Theory]
    [InlineData("tenantId")]
    [InlineData("exportId")]
    [InlineData("downloadId")]
    [InlineData("blobId")]
    public async Task PackageRejectsCallerSuppliedIdentifiers(string field)
    {
        // GIVEN an authenticated member supplying identifiers outside the scope-only contract.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        await using var factory = app.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IBlobStore>();
            services.AddSingleton<IBlobStore>(new FileSystemBlobStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        }));
        using var client = factory.CreateClient();
        await LoginAsync(client);
        // WHEN requesting a package with a guessed identifier THEN binding rejects the request.
        var response = await PostAsync(client, "/api/items/export-package", new Dictionary<string, object> { ["scope"] = "all", [field] = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentDisposition);
    }

    internal static async Task<string> ReadAsync(ZipArchive zip, string path)
    {
        using var reader = new StreamReader(zip.GetEntry(path)!.Open());
        return await reader.ReadToEndAsync();
    }
}
