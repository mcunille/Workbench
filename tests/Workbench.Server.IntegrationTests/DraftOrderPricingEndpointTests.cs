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
public sealed class DraftOrderPricingEndpointTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData("/api/purchase-order-drafts")]
    [InlineData("/api/v2/purchase-order-drafts")]
    [InlineData("/api/v3/purchase-order-drafts")]
    [InlineData("/api/v4/purchase-order-drafts")]
    public async Task RetiredRoutesRejectWithoutReadingMalformedBodies(string path)
    {
        // GIVEN an obsolete caller sending a non-JSON request without beta revision or login.
        await using var application = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>();
        using var client = application.CreateClient();
        // WHEN it posts to a removed route THEN the generic unsupported response does not bind the old body.
        var response = await client.PostAsync(path, new StringContent("not-json"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("api_contract_unsupported", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
    private const string Path = "/api/beta/purchase-order-drafts";
    [Fact]
    public async Task CalculationIsPrivateProtectedAndDoesNotPersist()
    {
        // GIVEN an unauthenticated browser and an exact per-unit draft.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        var draft = DraftOrderPricingTests.Empty with { Entries = [DraftOrderPricingTests.Line with { Quantity = "12.5", Price = "20" }] };
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
        var draft = DraftOrderPricingTests.Empty with { Entries = [DraftOrderPricingTests.Line with { Quantity = "12.5", Price = "20", SupplierSku = "Gem 17", ItemType = "Gemstone" }] };
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
    public async Task RetiredRoutesRejectEvenPreviouslySuccessfulRequests()
    {
        // GIVEN successful beta create and delete receipts with the same storage format as the former V4 API.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient(); await LoginAsync(client);
        var request = new CreateDraftOrderRequest(Guid.NewGuid(), DraftOrderPricingTests.Empty);
        var saved = (await (await SendAsync(client, HttpMethod.Post, Path, request)).Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        var delete = new DeleteDraftOrderRequest(Guid.NewGuid(), saved.SavedVersion);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Delete, Path + "/" + saved.DraftOrderId, delete)).StatusCode);
        // WHEN old callers submit identical requests through any retired route THEN reload is required without returning a receipt.
        foreach (var version in new[] { "", "/v2", "/v3", "/v4" })
        {
            var retired = "/api" + version + "/purchase-order-drafts";
            foreach (var response in new[] {
                await SendAsync(client, HttpMethod.Post, retired, request),
                await SendAsync(client, HttpMethod.Put, retired + "/" + saved.DraftOrderId, new UpdateDraftOrderRequest(Guid.NewGuid(), saved.SavedVersion, request.Draft)),
                await SendAsync(client, HttpMethod.Delete, retired + "/" + saved.DraftOrderId, delete) })
            {
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Equal("api_contract_unsupported", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            }
        }
        // AND current beta retries still return their receipt without reviving the deleted draft.
        Assert.True((await (await SendAsync(client, HttpMethod.Post, Path, request)).Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!.Replayed);
        Assert.True((await (await SendAsync(client, HttpMethod.Delete, Path + "/" + saved.DraftOrderId, delete)).Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!.Replayed);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Path + "/" + saved.DraftOrderId)).StatusCode);
    }
}
