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
    private const string Path = "/api/v3/purchase-order-drafts";
    [Fact]
    public async Task CalculationIsPrivateProtectedAndDoesNotPersist()
    {
        // GIVEN an unauthenticated browser and an exact mixed-unit draft.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        var draft = DraftOrderInputV3Tests.Empty with { Entries = [DraftOrderInputV3Tests.Line with { PricingUnit = "carat", PricingQuantity = "12.5" }] };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Path + "/calculate", new CalculateDraftOrderRequest(draft))).StatusCode);
        await LoginAsync(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path + "/calculate", new CalculateDraftOrderRequest(draft))).StatusCode);
        // WHEN calculated with authentication and antiforgery THEN the server returns an exact private estimate without creating a draft.
        var response = await SendAsync(client, HttpMethod.Post, Path + "/calculate", new CalculateDraftOrderRequest(draft));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("250.0000", (await response.Content.ReadFromJsonAsync<DraftCalculationResponse>())!.MerchandiseEstimate);
        Assert.Empty((await client.GetFromJsonAsync<DraftOrderPageResponseV2>(Path))!.Items);
        var invalid = await SendAsync(client, HttpMethod.Post, Path + "/calculate", new CalculateDraftOrderRequest(draft with { Entries = [draft.Entries[0] with { Quantity = "0" }] }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var problem = await invalid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("draft_validation_failed", problem.GetProperty("code").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("draft.entries[0].quantity", out _));
    }
    [Fact]
    public async Task StructuredSaveReadLegacyProjectionAndTenantIsolationAgree()
    {
        // GIVEN an authenticated owner saving stone counts priced by explicit weight.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient(); await LoginAsync(client);
        var draft = DraftOrderInputV3Tests.Empty with { Entries = [DraftOrderInputV3Tests.Line with { PricingUnit = "carat", PricingQuantity = "12.5", SupplierSku = "Gem 17", ItemType = "Gemstone" }] };
        var request = new CreateDraftOrderRequestV3(Guid.NewGuid(), draft);
        // WHEN saved and read THEN quantities and server calculation survive while old reads never reinterpret unit price.
        var saved = await SendAsync(client, HttpMethod.Post, Path, request);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var receipt = (await saved.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        var detail = (await client.GetFromJsonAsync<DraftOrderResponseV3>($"{Path}/{receipt.DraftOrderId}"))!;
        Assert.Equal("250.0000", detail.Calculation.MerchandiseEstimate);
        Assert.Equal("12.5000", Assert.Single(detail.Draft.Entries).PricingQuantity);
        Assert.Equal(draft.Entries[0].Id, detail.Draft.Entries[0].Id);
        var legacy = (await client.GetFromJsonAsync<DraftOrderResponseV2>($"/api/v2/purchase-order-drafts/{receipt.DraftOrderId}"))!;
        Assert.Null(Assert.Single(legacy.Draft.Entries).IndicativePrice);
        using var other = app.CreateClient(); await LoginAsync(other, "other@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Path}/{receipt.DraftOrderId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(other, HttpMethod.Put, $"{Path}/{receipt.DraftOrderId}", new UpdateDraftOrderRequestV3(Guid.NewGuid(), detail.Version, draft))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(other, HttpMethod.Delete, $"{Path}/{receipt.DraftOrderId}", new DeleteDraftOrderRequest(Guid.NewGuid(), detail.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Delete, $"{Path}/{receipt.DraftOrderId}", new DeleteDraftOrderRequest(Guid.NewGuid(), detail.Version))).StatusCode);
        var replay = await SendAsync(client, HttpMethod.Post, Path, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True((await replay.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!.Replayed);
    }
    [Fact]
    public async Task UnmatchedOldWritesRequireReload()
    {
        // GIVEN a current authenticated owner using an old write contract.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient(); await LoginAsync(client);
        // WHEN no historical receipt matches THEN both old versions reject without mutation.
        foreach (var path in new[] { "/api/purchase-order-drafts", "/api/v2/purchase-order-drafts" })
        {
            object draft = path.Contains("/v2/") ? DraftOrderInputV3.Legacy(DraftOrderInputV3Tests.Empty) : PurchasingIdentityInput.Legacy(DraftOrderInputV3.Legacy(DraftOrderInputV3Tests.Empty));
            var response = await SendAsync(client, HttpMethod.Post, path, new { requestId = Guid.NewGuid(), draft });
            Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
            Assert.Equal("draft_contract_reload_required", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }
}
