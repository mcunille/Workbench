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
public sealed class PurchaseOrderEndpointTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task CommitmentAndAmendmentRetainImmutableHistoryAndExactRetries()
    {
        // GIVEN a saved purchase with an explicit supplier and unknown price.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient(); await LoginAsync(client);
        var draft = DraftOrderPricingTests.Empty with { SupplierName = "Supplier Alpha", Entries = [DraftOrderPricingTests.Line with { Description = "Sapphire", Price = null }] };
        var create = new CreateDraftOrderRequest(Guid.NewGuid(), draft);
        var created = await SendAsync(client, HttpMethod.Post, "/api/beta/purchase-order-drafts", create);
        var receipt = (await created.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        var path = $"/api/beta/purchase-orders/{receipt.DraftOrderId}";
        var commitPath = $"/api/beta/purchase-order-drafts/{receipt.DraftOrderId}/commit";
        var commit = new { requestId = Guid.NewGuid(), expectedVersion = receipt.SavedVersion, orderDate = "2026-09-11" };
        // WHEN the saved purchase is recorded as ordered THEN the entered date and unknown estimate survive.
        var committed = await SendAsync(client, HttpMethod.Post, commitPath, commit);
        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        var saved = await committed.Content.ReadFromJsonAsync<JsonElement>();
        var current = await client.GetFromJsonAsync<JsonElement>(path);
        Assert.Equal("Ordered", current.GetProperty("state").GetString());
        Assert.Equal("2026-09-11", current.GetProperty("orderDate").GetString());
        Assert.Equal(1, current.GetProperty("calculation").GetProperty("incompleteLineCount").GetInt32());
        var original = await client.GetStringAsync(path + "/revisions/1");
        // WHEN correcting quantities with a reason THEN a second revision is appended without changing the first.
        var amendment = new { requestId = Guid.NewGuid(), expectedVersion = saved.GetProperty("savedVersion").GetString(), orderDate = "2026-09-12", reason = "Supplier corrected quantity", draft = draft with { Entries = [draft.Entries[0] with { Quantity = "20" }] } };
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Post, path + "/amendments", amendment)).StatusCode);
        Assert.Equal(original, await client.GetStringAsync(path + "/revisions/1"));
        // AND exact retries retain the original receipt even after later revisions.
        var replay = await SendAsync(client, HttpMethod.Post, commitPath, commit);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True((await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("replayed").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Post, "/api/beta/purchase-order-drafts", create)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"/api/beta/purchase-order-drafts/{receipt.DraftOrderId}")).StatusCode);
    }
    [Fact]
    public async Task PurchaseValidationAndStrictTransportPreserveTheSavedDraft()
    {
        // GIVEN an authenticated owner and incomplete draft.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient(); await LoginAsync(client);
        var response = await SendAsync(client, HttpMethod.Post, "/api/beta/purchase-order-drafts", new CreateDraftOrderRequest(Guid.NewGuid(), DraftOrderPricingTests.Empty));
        var saved = (await response.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        var path = $"/api/beta/purchase-order-drafts/{saved.DraftOrderId}/commit";
        var request = new { requestId = Guid.NewGuid(), expectedVersion = saved.SavedVersion, orderDate = "2026-02-30" };
        // WHEN submitting missing supplier/lines/date THEN errors name all actionable fields without changing state.
        var invalid = await SendAsync(client, HttpMethod.Post, path, request);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("orderDate", out _)); Assert.True(errors.TryGetProperty("draft.supplierName", out _)); Assert.True(errors.TryGetProperty("draft.entries", out _));
        Assert.Equal("Draft", (await client.GetFromJsonAsync<JsonElement>($"/api/beta/purchase-orders/{saved.DraftOrderId}")).GetProperty("state").GetString());
        // AND antiforgery and unknown-property binding remain enforced on the new routes.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(client, HttpMethod.Post, path, new { request.requestId, request.expectedVersion, request.orderDate, extra = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/beta/purchase-orders?state=Cancelled")).StatusCode);
        Assert.True((await client.GetAsync("/api/beta/purchase-orders")).Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("commit")]
    public async Task ConcurrentDraftCommandsSerializeWithCommitment(string competing)
    {
        // GIVEN two authenticated sessions observing the same saved row version.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient(); using var other = app.CreateClient(); await LoginAsync(client); await LoginAsync(other);
        var draft = DraftOrderPricingTests.Empty with { SupplierName = "Supplier", Entries = [DraftOrderPricingTests.Line with { Description = "Stone" }] };
        var response = await SendAsync(client, HttpMethod.Post, "/api/beta/purchase-order-drafts", new CreateDraftOrderRequest(Guid.NewGuid(), draft));
        var saved = (await response.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        var path = $"/api/beta/purchase-order-drafts/{saved.DraftOrderId}";
        var commit = new { requestId = Guid.NewGuid(), expectedVersion = saved.SavedVersion, orderDate = "2026-09-11" };
        // WHEN commit races an edit, deletion or another commit THEN exactly one expected-version write succeeds.
        var first = SendAsync(client, HttpMethod.Post, path + "/commit", commit);
        var second = competing switch
        {
            "update" => SendAsync(other, HttpMethod.Put, path, new UpdateDraftOrderRequest(Guid.NewGuid(), saved.SavedVersion, draft with { Notes = "Concurrent correction" })),
            "delete" => SendAsync(other, HttpMethod.Delete, path, new DeleteDraftOrderRequest(Guid.NewGuid(), saved.SavedVersion)),
            _ => SendAsync(other, HttpMethod.Post, path + "/commit", new { requestId = Guid.NewGuid(), expectedVersion = saved.SavedVersion, orderDate = "2026-09-12" })
        };
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, r => r.IsSuccessStatusCode);
        Assert.Single(results, r => r.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound);
        if (results[0].IsSuccessStatusCode)
        {
            var revisions = await client.GetFromJsonAsync<JsonElement>($"/api/beta/purchase-orders/{saved.DraftOrderId}/revisions");
            Assert.Equal(1, revisions.GetProperty("items").GetArrayLength());
        }
    }

    [Fact]
    public async Task ConcurrentAmendmentsAndChangedRetriesCannotAppendExtraRevisions()
    {
        // GIVEN an ordered purchase observed in two authenticated sessions.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient(); using var other = app.CreateClient(); await LoginAsync(client); await LoginAsync(other);
        var draft = DraftOrderPricingTests.Empty with { SupplierName = "Supplier", Entries = [DraftOrderPricingTests.Line with { Description = "Stone" }] };
        var response = await SendAsync(client, HttpMethod.Post, "/api/beta/purchase-order-drafts", new CreateDraftOrderRequest(Guid.NewGuid(), draft));
        var saved = (await response.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        var commitPath = $"/api/beta/purchase-order-drafts/{saved.DraftOrderId}/commit";
        var commit = new { requestId = Guid.NewGuid(), expectedVersion = saved.SavedVersion, orderDate = "2026-09-11" };
        var receipt = (await (await SendAsync(client, HttpMethod.Post, commitPath, commit)).Content.ReadFromJsonAsync<SavePurchaseOrderResponse>())!;
        var path = $"/api/beta/purchase-orders/{saved.DraftOrderId}";
        var amendment = new AmendPurchaseOrderRequest(Guid.NewGuid(), receipt.SavedVersion, "2026-09-11", "Supplier correction", draft with { Notes = "First correction" });
        // WHEN two amendments race THEN one succeeds and one conflicts without duplicate evidence.
        var results = await Task.WhenAll(SendAsync(client, HttpMethod.Post, path + "/amendments", amendment), SendAsync(other, HttpMethod.Post, path + "/amendments", amendment with { RequestId = Guid.NewGuid(), Draft = draft with { Notes = "Other correction" } }));
        Assert.Single(results, r => r.IsSuccessStatusCode); Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>(path + "/revisions")).GetProperty("items").GetArrayLength());
        // AND a changed replay payload conflicts even though its original commitment succeeded.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Post, commitPath, new { commit.requestId, commit.expectedVersion, orderDate = "2026-09-12" })).StatusCode);
    }
}
