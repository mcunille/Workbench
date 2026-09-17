// Copyright (c) 2026 The White Stag Collection.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Purchasing;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;
namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class DraftOrderEndpointV3Tests(SqlServerFixture sqlServer)
{
    private const string Path = "/api/beta/purchase-order-drafts";
    [Fact]
    public async Task CalculationIsPrivateProtectedAndDoesNotPersist()
    {
        // GIVEN an unauthenticated browser and an exact per-unit draft.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        var draft = DraftOrderInputV4Tests.Empty with { Entries = [DraftOrderInputV4Tests.Line with { Quantity = "12.5", Price = "20" }] };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Path + "/calculate", new CalculateDraftOrderRequest(draft))).StatusCode);
        await LoginAsync(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path + "/calculate", new CalculateDraftOrderRequest(draft))).StatusCode);
        // WHEN calculated with authentication and antiforgery THEN the server returns an exact private estimate without creating a draft.
        var response = await SendAsync(client, HttpMethod.Post, Path + "/calculate", new CalculateDraftOrderRequest(draft));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("250.0000", (await response.Content.ReadFromJsonAsync<DraftCalculationResponse>())!.MerchandiseEstimate);
        Assert.Empty((await client.GetFromJsonAsync<DraftOrderPageResponse>(Path))!.Items);
        var invalid = await SendAsync(client, HttpMethod.Post, Path + "/calculate", new CalculateDraftOrderRequest(draft with { Entries = [draft.Entries[0] with { Quantity = "0" }] }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var problem = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("draft_validation_failed", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("draft.entries[0].quantity", out _));
    }
    [Fact]
    public async Task StructuredSaveReadAndTenantIsolationAgree()
    {
        // GIVEN an authenticated owner saving priced stone counts.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient(); await LoginAsync(client);
        var draft = DraftOrderInputV4Tests.Empty with { Entries = [DraftOrderInputV4Tests.Line with { Quantity = "12.5", Price = "20", SupplierSku = "Gem 17", ItemType = "Gemstone" }] };
        var request = new CreateDraftOrderRequest(Guid.NewGuid(), draft);
        // WHEN saved and read THEN the current price mode and exact estimate survive.
        var saved = await SendAsync(client, HttpMethod.Post, Path, request);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var receipt = (await saved.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        var detail = (await client.GetFromJsonAsync<DraftOrderResponse>($"{Path}/{receipt.DraftOrderId}"))!;
        Assert.Equal("250.0000", detail.Calculation.MerchandiseEstimate);
        Assert.Equal("12.5000", Assert.Single(detail.Draft.Entries).Quantity);
        Assert.Equal(draft.Entries[0].Id, detail.Draft.Entries[0].Id);
        using var other = app.CreateClient(); await LoginAsync(other, "other@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Path}/{receipt.DraftOrderId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(other, HttpMethod.Put, $"{Path}/{receipt.DraftOrderId}", new UpdateDraftOrderRequest(Guid.NewGuid(), detail.Version, draft))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(other, HttpMethod.Delete, $"{Path}/{receipt.DraftOrderId}", new DeleteDraftOrderRequest(Guid.NewGuid(), detail.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Delete, $"{Path}/{receipt.DraftOrderId}", new DeleteDraftOrderRequest(Guid.NewGuid(), detail.Version))).StatusCode);
        var replay = await SendAsync(client, HttpMethod.Post, Path, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True((await replay.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!.Replayed);
    }
    [Fact]
    public async Task RetiredV4ResolvesExactSaveAndDeletionWithoutRevisionOrNewWrites()
    {
        // GIVEN successful current-format creation and deletion receipts whose browser lost its responses.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient(); await LoginAsync(client);
        var request = new CreateDraftOrderRequest(Guid.NewGuid(), DraftOrderInputV4Tests.Empty);
        var created = await SendAsync(client, HttpMethod.Post, Path, request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var saved = (await created.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        var delete = new DeleteDraftOrderRequest(Guid.NewGuid(), saved.SavedVersion);
        var removed = await SendAsync(client, HttpMethod.Delete, Path + "/" + saved.DraftOrderId, delete);
        var deletion = (await removed.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        client.DefaultRequestHeaders.Remove(Workbench.Server.Http.BetaApiContractMiddleware.HeaderName);
        const string retired = "/api/v4/purchase-order-drafts";
        // WHEN the old browser retries the exact original requests without the new revision mechanism.
        var replay = await SendAsync(client, HttpMethod.Post, retired, request);
        var deletionReplay = await SendAsync(client, HttpMethod.Delete, retired + "/" + saved.DraftOrderId, delete);
        // THEN only the original evidence returns, preserving the tombstone and refusing changed or new work.
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(saved with { Replayed = true }, await replay.Content.ReadFromJsonAsync<SaveDraftOrderResponse>());
        Assert.Equal(deletion with { Replayed = true }, await deletionReplay.Content.ReadFromJsonAsync<SaveDraftOrderResponse>());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Path + "/" + saved.DraftOrderId)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Post, retired, request with { Draft = request.Draft with { Title = "Different" } })).StatusCode);
        Assert.Equal(HttpStatusCode.UpgradeRequired, (await SendAsync(client, HttpMethod.Post, retired, request with { RequestId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(retired, request)).StatusCode);
        using var other = app.CreateClient(); await LoginAsync(other, "other@example.com");
        Assert.Equal(HttpStatusCode.UpgradeRequired, (await SendAsync(other, HttpMethod.Post, retired, request)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<DraftOrderPageResponse>(Path))!.Items);
    }

    [Fact]
    public async Task UnmatchedOldWritesRequireReload()
    {
        // GIVEN a current authenticated owner using a retired write contract.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient(); await LoginAsync(client);
        var historical = DraftOrderInputV3Tests.Empty;
        // WHEN no historical receipt matches THEN every old contract rejects without mutation.
        foreach (var version in new[] { 1, 2, 3, 4 })
        {
            var path = version == 1 ? "/api/purchase-order-drafts" : $"/api/v{version}/purchase-order-drafts";
            object draft = version switch
            {
                1 => PurchasingIdentityInput.Legacy(DraftOrderInputV3.Legacy(historical)),
                2 => DraftOrderInputV3.Legacy(historical),
                3 => historical,
                _ => DraftOrderInputV4Tests.Empty
            };
            var response = await SendAsync(client, HttpMethod.Post, path, new { requestId = Guid.NewGuid(), draft });
            Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
            Assert.Equal("api_contract_unsupported", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            var deletion = await SendAsync(client, HttpMethod.Delete, path + "/" + Guid.NewGuid(), new DeleteDraftOrderRequest(Guid.NewGuid(), Convert.ToBase64String(new byte[8])));
            Assert.Equal(HttpStatusCode.UpgradeRequired, deletion.StatusCode);
        }
        Assert.Empty((await client.GetFromJsonAsync<DraftOrderPageResponse>(Path))!.Items);
    }
}
