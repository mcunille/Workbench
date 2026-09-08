// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ItemExportEndpointTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task ExportRequiresExplicitScopeAndReturnsEmptyWithoutAFile()
    {
        // GIVEN an authenticated member with no items.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        await LoginAsync(client);
        // WHEN exporting without a scope, with an invalid scope, or an empty valid scope.
        var missing = await PostAsync(client, "/api/items/export", new { });
        var invalid = await PostAsync(client, "/api/items/export", new { scope = "archived" });
        var empty = await PostAsync(client, "/api/items/export", new { scope = "active" });
        // THEN intent is required and an empty export never produces an attachment.
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, empty.StatusCode);
        Assert.Null(empty.Content.Headers.ContentDisposition);
    }

    [Fact]
    public async Task ExportIncludesAllPagesAndExplicitArchiveScopeButNeverAnotherTenant()
    {
        // GIVEN more than one page of records, including an archived item and a foreign item.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        using var other = app.CreateClient();
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
        await PostAsync(other, "/api/items", new { creationRequestId = Guid.NewGuid(), name = "Foreign secret" });
        // WHEN exporting each explicit scope without pagination or search parameters.
        var active = await PostAsync(client, "/api/items/export", new { scope = "active" });
        var all = await PostAsync(client, "/api/items/export", new { scope = "all" });
        // THEN all selected rows and only this tenant are included with a complete private attachment.
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        var activeCsv = await active.Content.ReadAsStringAsync();
        var allCsv = await all.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ids[0], activeCsv);
        Assert.All(ids, id => Assert.Contains(id, allCsv));
        Assert.DoesNotContain("Foreign secret", allCsv);
        Assert.Equal(52, allCsv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal("text/csv", all.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", all.Content.Headers.ContentDisposition?.DispositionType);
        Assert.True(all.Headers.CacheControl?.NoStore);
        var bytes = await all.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes.Length, all.Content.Headers.ContentLength);
        Assert.Equal(new byte[] { 0xef, 0xbb, 0xbf }, bytes[..3]);
    }

    [Fact]
    public async Task ExportRequiresAuthenticationAndAntiforgery()
    {
        // GIVEN an anonymous client and then a member lacking the antiforgery header.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        // WHEN requesting a private export.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/items/export", new { scope = "all" })).StatusCode);
        await LoginAsync(client);
        // THEN authentication alone does not bypass antiforgery validation.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/items/export", new { scope = "all" })).StatusCode);
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
