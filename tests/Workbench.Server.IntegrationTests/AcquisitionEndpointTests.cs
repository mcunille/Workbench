// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Inventory;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class AcquisitionEndpointTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task AuthenticationInputVersionsAndTenantBoundariesProtectContext()
    {
        // GIVEN an anonymous caller and then a saved private acquisition.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/items/{Guid.NewGuid()}/acquisition")).StatusCode);
        await LoginAsync(client);
        var item = await CreateItemAsync(client);
        var path = $"/api/items/{item.Id}/acquisition";
        var request = new CreateAcquisitionRequest(Guid.NewGuid(), item.Version, "Gift", "Family", 2024, null, null, "Original");
        // WHEN invalid fields, tokens and unknown properties are submitted THEN no row is created.
        foreach (var invalid in new object[] { request with { CreationRequestId = Guid.Empty }, request with { ExpectedItemVersion = "AQ==" },
            request with { ExpectedItemVersion = null }, request with { ExpectedItemVersion = "not base64" }, request with { Method = null },
            request with { Year = 2023, Month = 2, Day = 29 }, request with { Year = 9999 }, request with { Notes = new string('n', 4001) },
            new { request.CreationRequestId, request.ExpectedItemVersion, request.Method, tenantId = Guid.NewGuid() } })
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(client, HttpMethod.Post, path, invalid)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, request)).StatusCode);
        var created = await SendAsync(client, HttpMethod.Post, path, request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var saved = (await created.Content.ReadFromJsonAsync<ItemAcquisitionResponse>())!;
        var updatePath = $"{path}/{saved.Acquisition!.Id}";
        var update = new UpdateAcquisitionRequest(saved.ItemVersion, saved.Acquisition.Version, "Trade", null, null, null, null, null);
        using var other = application.CreateClient();
        await LoginAsync(other, "other@example.com");
        foreach (var id in new[] { item.Id, Guid.NewGuid() })
        {
            // WHEN a foreign or missing identifier is addressed THEN the response is indistinguishable.
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/items/{id}/acquisition")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(other, HttpMethod.Post, $"/api/items/{id}/acquisition", request)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(other, HttpMethod.Put, $"/api/items/{id}/acquisition/{saved.Acquisition.Id}", update)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, HttpMethod.Put, $"{path}/{Guid.NewGuid()}", update)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(client, HttpMethod.Put, updatePath, update with { ExpectedAcquisitionVersion = "AQ==" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(client, HttpMethod.Put, updatePath,
            new { update.ExpectedItemVersion, update.ExpectedAcquisitionVersion, update.Method, archivedAtUtc = DateTimeOffset.UtcNow })).StatusCode);
        Assert.Equal("acquisition_version_conflict", await ConflictCodeAsync(await SendAsync(client, HttpMethod.Put, updatePath, update with { ExpectedItemVersion = item.Version })));
        Assert.Equal("acquisition_version_conflict", await ConflictCodeAsync(await SendAsync(client, HttpMethod.Put, updatePath, update with { ExpectedAcquisitionVersion = item.Version })));
        Assert.Equal("item_acquisition_conflict", await ConflictCodeAsync(await SendAsync(client, HttpMethod.Post, path, request with { CreationRequestId = Guid.NewGuid() })));
        foreach (var changed in new[] { request with { Notes = "original" }, request with { Notes = "Original " }, request with { Year = null }, request with { Source = "family" } })
            Assert.Equal("acquisition_request_conflict", await ConflictCodeAsync(await SendAsync(client, HttpMethod.Post, path, changed)));
        // AND current-version mutation on archived items is rejected, while saved context remains readable.
        var archived = await SendAsync(client, HttpMethod.Post, $"/api/items/{item.Id}/archive", new { expectedVersion = saved.ItemVersion });
        var archivedItem = (await archived.Content.ReadFromJsonAsync<ItemDetailResponse>())!;
        Assert.Equal("item_archived", await ConflictCodeAsync(await SendAsync(client, HttpMethod.Put, updatePath, update with { ExpectedItemVersion = archivedItem.Version })));
        Assert.Equal(saved.Acquisition, (await client.GetFromJsonAsync<ItemAcquisitionResponse>(path))!.Acquisition);
        var empty = await CreateItemAsync(client);
        var emptyArchived = await SendAsync(client, HttpMethod.Post, $"/api/items/{empty.Id}/archive", new { expectedVersion = empty.Version });
        var emptyVersion = (await emptyArchived.Content.ReadFromJsonAsync<ItemDetailResponse>())!.Version;
        Assert.Equal("item_archived", await ConflictCodeAsync(await SendAsync(client, HttpMethod.Post, $"/api/items/{empty.Id}/acquisition", request with { CreationRequestId = Guid.NewGuid(), ExpectedItemVersion = emptyVersion })));
    }

    private static async Task<string?> ConflictCodeAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();
    }

    [Fact]
    public async Task OptionalContextPersistsAndOriginalRequestReplaysAfterCorrectionAndArchive()
    {
        // GIVEN a saved item with no acquisition.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        await LoginAsync(client);
        var item = await CreateItemAsync(client);
        var path = $"/api/items/{item.Id}/acquisition";
        var empty = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await empty.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("acquisition").ValueKind);
        var request = new { creationRequestId = Guid.NewGuid(), expectedItemVersion = item.Version, method = "Gift", source = "  Family  ", year = 2024, month = 2, day = 29, notes = "  Original\nnotes  " };
        // WHEN recording and then correcting acquisition context.
        var created = await SendAsync(client, HttpMethod.Post, path, request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var saved = await created.Content.ReadFromJsonAsync<JsonElement>();
        var acquisition = saved.GetProperty("acquisition");
        Assert.Equal("Family", acquisition.GetProperty("source").GetString());
        Assert.Equal(request.notes, acquisition.GetProperty("notes").GetString());
        Assert.NotEqual(item.Version, saved.GetProperty("itemVersion").GetString());
        var edited = await SendAsync(client, HttpMethod.Put, $"{path}/{acquisition.GetProperty("id").GetGuid()}",
            new { expectedItemVersion = saved.GetProperty("itemVersion").GetString(), expectedAcquisitionVersion = acquisition.GetProperty("version").GetString(), method = "Unknown" });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var corrected = await edited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(acquisition.GetProperty("id").GetGuid(), corrected.GetProperty("acquisition").GetProperty("id").GetGuid());
        // THEN an original retry returns the current context, even after archive.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Post, $"/api/items/{item.Id}/archive", new { expectedVersion = corrected.GetProperty("itemVersion").GetString() })).StatusCode);
        var replay = await SendAsync(client, HttpMethod.Post, path, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("Unknown", (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("acquisition").GetProperty("method").GetString());
        using var laterSession = application.CreateClient();
        await LoginAsync(laterSession);
        Assert.Equal(HttpStatusCode.OK, (await laterSession.GetAsync(path)).StatusCode);
        Assert.True(replay.Headers.CacheControl?.NoStore);
    }

    internal static async Task<ItemDetailResponse> CreateItemAsync(HttpClient client) =>
        (await (await SendAsync(client, HttpMethod.Post, "/api/items", new { creationRequestId = Guid.NewGuid(), name = "Acquisition test piece" })).Content.ReadFromJsonAsync<ItemDetailResponse>())!;

    internal static async Task LoginAsync(HttpClient client, string email = "member@example.com") =>
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(client, HttpMethod.Post, "/api/auth/login", new { email, password = AuthTestApplication.AdminPassword })).StatusCode);

    internal static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object body)
    {
        var token = await client.GetFromJsonAsync<JsonElement>("/api/auth/antiforgery");
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", token.GetProperty("requestToken").GetString());
        return await client.SendAsync(request);
    }
}
