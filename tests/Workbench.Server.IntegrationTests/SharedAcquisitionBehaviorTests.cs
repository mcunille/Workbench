// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Inventory;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SharedAcquisitionBehaviorTests(SqlServerFixture sqlServer)
{
    internal static LinkAcquisitionRequest Link(ItemAcquisitionResponse current, AcquisitionResponse? target) =>
        new(current.ItemVersion, current.Acquisition?.Id, current.Acquisition?.Version, target?.Id, target?.Version);

    internal static async Task<ItemAcquisitionResponse> CreateContext(HttpClient client, ItemDetailResponse item, string source = "Fair") =>
        (await (await SendAsync(client, HttpMethod.Post, $"/api/items/{item.Id}/acquisition",
            new CreateAcquisitionRequest(Guid.NewGuid(), item.Version, "Purchase", source, 2024, null, null, null)))
            .Content.ReadFromJsonAsync<ItemAcquisitionResponse>())!;

    internal static async Task<ItemAcquisitionResponse> Current(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<ItemAcquisitionResponse>($"/api/items/{id}/acquisition"))!;

    [Fact]
    public async Task ThreePiecesShareEditsAndArchiveRestoreRetainsRelationships()
    {
        // GIVEN three individually recorded pieces.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        await LoginAsync(client);
        var items = new[] { await CreateItemAsync(client), await CreateItemAsync(client), await CreateItemAsync(client) };
        var saved = await CreateContext(client, items[0]);
        foreach (var item in items.Skip(1))
        {
            var result = await SendAsync(client, HttpMethod.Put, $"/api/items/{item.Id}/acquisition-link", Link(new(null, item.Version), saved.Acquisition));
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            saved = (await result.Content.ReadFromJsonAsync<ItemAcquisitionResponse>())!;
        }
        // WHEN one piece is archived and shared fields are edited through an active sibling.
        var archivedContext = await Current(client, items[2].Id);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Post, $"/api/items/{items[2].Id}/archive", new { expectedVersion = archivedContext.ItemVersion })).StatusCode);
        var editContext = await Current(client, items[0].Id);
        var edited = await SendAsync(client, HttpMethod.Put, $"/api/items/{items[0].Id}/acquisition/{saved.Acquisition!.Id}",
            new UpdateAcquisitionRequest(editContext.ItemVersion, editContext.Acquisition!.Version, "Trade", "Corrected fair", 2023, 2, null, "Shared correction"));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        // THEN all three read the one corrected identity, and archived browsing is opt-in.
        foreach (var item in items) Assert.Equal("Corrected fair", (await Current(client, item.Id)).Acquisition!.Source);
        var path = $"/api/acquisitions/{saved.Acquisition.Id}/items";
        Assert.Equal(2, (await client.GetFromJsonAsync<AcquisitionItemsResponse>(path))!.Items.Count);
        var all = (await client.GetFromJsonAsync<AcquisitionItemsResponse>(path + "?includeArchived=true"))!.Items;
        Assert.Equal(3, all.Count);
        var archived = Assert.Single(all, item => item.ArchivedAtUtc is not null);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Put, $"/api/items/{archived.Id}/acquisition-link", Link(await Current(client, archived.Id), null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Post, $"/api/items/{archived.Id}/restore", new { expectedVersion = archived.Version })).StatusCode);
        Assert.Equal(3, (await client.GetFromJsonAsync<AcquisitionItemsResponse>(path))!.Items.Count);
    }

    [Fact]
    public async Task AtomicReplacementLastRemovalAndCheckedNoOpPreserveSavedRecords()
    {
        // GIVEN two contexts, each with one original piece.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        await LoginAsync(client);
        var first = await CreateItemAsync(client);
        var second = await CreateItemAsync(client);
        var old = await CreateContext(client, first, "Old");
        var target = await CreateContext(client, second, "New");
        var command = Link(old, target.Acquisition);
        // WHEN replacement uses a stale target THEN the old link and item version survive exactly.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Put, $"/api/items/{first.Id}/acquisition-link", command with { TargetAcquisitionVersion = first.Version })).StatusCode);
        Assert.Equal(old, await Current(client, first.Id));
        // WHEN replacing atomically THEN the old empty context remains discoverable.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, $"/api/items/{first.Id}/acquisition-link", command)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<AcquisitionItemsResponse>($"/api/acquisitions/{old.Acquisition!.Id}/items"))!.Items);
        Assert.Single((await client.GetFromJsonAsync<AcquisitionPageResponse>("/api/acquisitions?search=Old"))!.Items);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Put, $"/api/items/{first.Id}/acquisition-link", command)).StatusCode);
        var now = await Current(client, first.Id);
        var noOp = await SendAsync(client, HttpMethod.Put, $"/api/items/{first.Id}/acquisition-link", Link(now, now.Acquisition));
        Assert.Equal(HttpStatusCode.OK, noOp.StatusCode);
        Assert.Equal(now, await noOp.Content.ReadFromJsonAsync<ItemAcquisitionResponse>());
        // AND no-op still checks versions rather than treating matching IDs as successful replay.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Put, $"/api/items/{first.Id}/acquisition-link", Link(now, now.Acquisition) with { ExpectedItemVersion = first.Version })).StatusCode);
        foreach (var item in new[] { first, second })
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, $"/api/items/{item.Id}/acquisition-link", Link(await Current(client, item.Id), null))).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<AcquisitionItemsResponse>($"/api/acquisitions/{target.Acquisition!.Id}/items"))!.Items);
        var untouched = (await client.GetFromJsonAsync<ItemDetailResponse>($"/api/items/{first.Id}"))!;
        Assert.Equal(first with { Version = untouched.Version }, untouched);
    }

    [Fact]
    public async Task DiscoveryIsBoundedStableSearchableAndTenantPrivate()
    {
        // GIVEN 45 tenant acquisitions sharing the same creation timestamp, plus one foreign acquisition.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        await LoginAsync(client);
        await using var admin = new SqlConnection(app.AdminConnectionString);
        await admin.OpenAsync();
        await using var seed = new SqlCommand("""
            DECLARE @i int=0;
            WHILE @i<45 BEGIN
                INSERT Inventory.Acquisitions (Id,TenantId,Method,Source,Notes,CreatedAtUtc,CreationRequestId)
                    VALUES (NEWID(),@tenant,'Gift',N'Fair',N'Rare find','2024-01-01',NEWID());
                SET @i+=1;
            END;
            INSERT Inventory.Acquisitions (Id,TenantId,Method,Source,CreatedAtUtc,CreationRequestId)
                VALUES (NEWID(),'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb','Gift',N'Fair','2024-01-01',NEWID());
            """, admin);
        seed.Parameters.AddWithValue("@tenant", AuthTestApplication.TenantId);
        await seed.ExecuteNonQueryAsync();
        // WHEN paging through matching notes THEN all and only this tenant's records appear once.
        var response = await client.GetAsync("/api/acquisitions?search=rare");
        Assert.True(response.Headers.CacheControl!.NoStore);
        var first = (await response.Content.ReadFromJsonAsync<AcquisitionPageResponse>())!;
        Assert.Equal(40, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        var second = (await client.GetFromJsonAsync<AcquisitionPageResponse>("/api/acquisitions?search=rare&cursor=" + Uri.EscapeDataString(first.NextCursor)))!;
        Assert.Equal(5, second.Items.Count);
        Assert.Null(second.NextCursor);
        Assert.Equal(45, first.Items.Concat(second.Items).Select(row => row.Id).Distinct().Count());
        foreach (var query in new[] { "cursor=bad", "cursor=", "search=" + new string('x', 201) })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/acquisitions?" + query)).StatusCode);
        using var other = app.CreateClient();
        await LoginAsync(other, "other@example.com");
        foreach (var id in new[] { first.Items[0].Id, Guid.NewGuid() })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/acquisitions/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/acquisitions/{id}/items?includeArchived=true")).StatusCode);
        }
    }
}
