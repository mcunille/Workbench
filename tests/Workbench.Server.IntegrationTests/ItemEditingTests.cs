// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Inventory;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ItemEditingTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task SaveNormalizesFieldsPreservesIdentityAndRejectsStaleIdenticalRetries()
    {
        // GIVEN an authenticated collector and an existing item.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        await LoginAsync(client, "member@example.com");
        var item = await CreateAsync(client);
        var body = new { expectedVersion = item.Version, name = "  Sapphire  ", notes = "  retained\nformat  ", location = "  Tray A  " };
        // WHEN replacing the descriptive fields with the checked version.
        var response = await SendAsync(client, HttpMethod.Put, $"/api/items/{item.Id}", body);
        // THEN the saved record retains its identity and creation metadata and advances its token.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = (await response.Content.ReadFromJsonAsync<ItemDetailResponse>())!;
        Assert.Equal(item.Id, saved.Id);
        Assert.Equal(item.CreatedAtUtc, saved.CreatedAtUtc);
        Assert.Equal(item.Photo, saved.Photo);
        Assert.Equal("Sapphire", saved.Name);
        Assert.Equal(body.notes, saved.Notes);
        Assert.Equal("Tray A", saved.Location);
        Assert.NotEqual(item.Version, saved.Version);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.Equal(saved, await client.GetFromJsonAsync<ItemDetailResponse>($"/api/items/{item.Id}"));
        Assert.Single((await client.GetFromJsonAsync<ItemPageResponse>("/api/items?q=Sapphire"))!.Items);
        Assert.Empty((await client.GetFromJsonAsync<ItemPageResponse>("/api/items?q=Original"))!.Items);
        // AND a response-loss retry with the old token cannot silently overwrite saved data.
        var stale = await SendAsync(client, HttpMethod.Put, $"/api/items/{item.Id}", body);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("item_version_conflict", (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        // AND an unchanged-value save with the current token still advances the version.
        var unchanged = await SendAsync(client, HttpMethod.Put, $"/api/items/{item.Id}", new { expectedVersion = saved.Version, saved.Name, saved.Notes, saved.Location });
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.NotEqual(saved.Version, (await unchanged.Content.ReadFromJsonAsync<ItemDetailResponse>())!.Version);
    }

    [Fact]
    public async Task AuthenticationValidationAndTenantIsolationProtectEdits()
    {
        // GIVEN a private item and an anonymous caller.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync($"/api/items/{Guid.NewGuid()}", new { name = "No" })).StatusCode);
        await LoginAsync(client, "member@example.com");
        var item = await CreateAsync(client);
        // WHEN invalid tokens, fields, or server-owned fields are submitted THEN none are accepted.
        foreach (var body in new object[] {
            new { name = "Name" }, new { expectedVersion = "bad", name = "Name" },
            new { expectedVersion = "AQ==", name = "Name" }, new { expectedVersion = item.Version, name = " \t" },
            new { expectedVersion = item.Version, name = new string('n', 201) },
            new { expectedVersion = item.Version, name = "Name", notes = new string('n', 4001) },
            new { expectedVersion = item.Version, name = "Name", location = new string('n', 201) },
            new { expectedVersion = item.Version, name = "Name", tenantId = Guid.NewGuid() },
            new { expectedVersion = item.Version, name = "Name", trackingKind = "Lot" },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(client, HttpMethod.Put, $"/api/items/{item.Id}", body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/items/{item.Id}", new { expectedVersion = item.Version, name = "Name" })).StatusCode);
        using var other = application.CreateClient();
        await LoginAsync(other, "other@example.com");
        // WHEN another tenant addresses the private ID THEN it is indistinguishable from a missing item.
        foreach (var id in new[] { item.Id, Guid.NewGuid() })
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(other, HttpMethod.Put, $"/api/items/{id}", new { expectedVersion = item.Version, name = "Name" })).StatusCode);
        Assert.Equal(item, await client.GetFromJsonAsync<ItemDetailResponse>($"/api/items/{item.Id}"));
    }

    [Fact]
    public async Task IndependentSessionsCompetingWithOneVersionHaveExactlyOneWinner()
    {
        // GIVEN two real authenticated sessions holding the same version.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var first = application.CreateClient();
        using var second = application.CreateClient();
        await LoginAsync(first, "member@example.com");
        await LoginAsync(second, "member@example.com");
        var item = await CreateAsync(first);
        // WHEN both submit a checked update concurrently.
        var results = await Task.WhenAll(SendAsync(first, HttpMethod.Put, $"/api/items/{item.Id}", new { expectedVersion = item.Version, name = "First" }),
            SendAsync(second, HttpMethod.Put, $"/api/items/{item.Id}", new { expectedVersion = item.Version, name = "Second" }));
        // THEN exactly one wins and its full committed result is the durable record.
        Assert.Single(results, result => result.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, result => result.StatusCode == HttpStatusCode.Conflict);
        var winner = await results.Single(result => result.StatusCode == HttpStatusCode.OK).Content.ReadFromJsonAsync<ItemDetailResponse>();
        Assert.Equal(winner, await first.GetFromJsonAsync<ItemDetailResponse>($"/api/items/{item.Id}"));
    }

    private static async Task<ItemDetailResponse> CreateAsync(HttpClient client) =>
        (await (await SendAsync(client, HttpMethod.Post, "/api/items", new { creationRequestId = Guid.NewGuid(), name = "Original" })).Content.ReadFromJsonAsync<ItemDetailResponse>())!;

    private static async Task LoginAsync(HttpClient client, string email) =>
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, HttpMethod.Post, "/api/auth/login", new { email, password = AuthTestApplication.AdminPassword })).StatusCode);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object body)
    {
        var token = await client.GetFromJsonAsync<JsonElement>("/api/auth/antiforgery");
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", token.GetProperty("requestToken").GetString());
        return await client.SendAsync(request);
    }
}
