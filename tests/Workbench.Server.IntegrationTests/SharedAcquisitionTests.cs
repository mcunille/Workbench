// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Inventory;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SharedAcquisitionTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task ConnectReplaceRemovePreservesIdentityAndCreationReplay()
    {
        // GIVEN separate recorded pieces and acquisition contexts.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        await LoginAsync(client);
        var first = await CreateItemAsync(client);
        var second = await CreateItemAsync(client);
        var original = new CreateAcquisitionRequest(Guid.NewGuid(), first.Version, "Gift", "Family", null, null, null, null);
        var created = await SendAsync(client, HttpMethod.Post, $"/api/items/{first.Id}/acquisition", original);
        var saved = (await created.Content.ReadFromJsonAsync<ItemAcquisitionResponse>())!;
        // WHEN a second piece connects to the same context.
        var connected = await SendAsync(client, HttpMethod.Put, $"/api/items/{second.Id}/acquisition-link", new
        {
            expectedItemVersion = second.Version,
            expectedAcquisitionId = (Guid?)null,
            expectedAcquisitionVersion = (string?)null,
            targetAcquisitionId = saved.Acquisition!.Id,
            targetAcquisitionVersion = saved.Acquisition.Version,
        });
        // THEN the shared context is discoverable with both identities.
        Assert.Equal(HttpStatusCode.OK, connected.StatusCode);
        var context = (await connected.Content.ReadFromJsonAsync<ItemAcquisitionResponse>())!;
        Assert.Equal(saved.Acquisition.Id, context.Acquisition!.Id);
        Assert.NotEqual(saved.Acquisition.Version, context.Acquisition.Version);
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/acquisitions/{saved.Acquisition.Id}/items");
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        var current = (await client.GetFromJsonAsync<ItemAcquisitionResponse>($"/api/items/{first.Id}/acquisition"))!;
        // WHEN the original connection is removed and its old creation request replayed.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, $"/api/items/{first.Id}/acquisition-link", new
        {
            expectedItemVersion = current.ItemVersion,
            expectedAcquisitionId = current.Acquisition!.Id,
            expectedAcquisitionVersion = current.Acquisition.Version,
            targetAcquisitionId = (Guid?)null,
            targetAcquisitionVersion = (string?)null,
        })).StatusCode);
        var replay = await SendAsync(client, HttpMethod.Post, $"/api/items/{first.Id}/acquisition", original);
        // THEN the replay returns current detached state without reconnecting or creating anything.
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Null((await replay.Content.ReadFromJsonAsync<ItemAcquisitionResponse>())!.Acquisition);
        var discoveries = await client.GetFromJsonAsync<JsonElement>("/api/acquisitions?search=Family");
        Assert.Single(discoveries.GetProperty("items").EnumerateArray());
    }
}
