// Copyright (c) 2026 The White Stag Collection.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Purchasing;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;
namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class DraftOrderFinancialEndpointTests(SqlServerFixture sqlServer)
{
    private const string Path = "/api/beta/purchase-order-drafts";
    [Fact]
    public async Task SupplierReplacementRequiresConfirmedChargeExplanation()
    {
        // GIVEN a confirmed charge paid to the order's one-off supplier.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient(); await LoginAsync(client);
        var charge = new DraftCharge(Guid.NewGuid(), "shipping", "Freight", "15", "supplier", null, "confirmed", null, "Original source");
        var draft = DraftOrderPricingTests.Empty with { SupplierName = "Supplier Alpha", Charges = [charge] };
        var created = await SendAsync(client, HttpMethod.Post, Path, new CreateDraftOrderRequest(Guid.NewGuid(), draft));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var receipt = (await created.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        var url = $"{Path}/{receipt.DraftOrderId}";
        var saved = (await client.GetFromJsonAsync<DraftOrderResponse>(url))!;
        var supplierResponse = await SendAsync(client, HttpMethod.Post, "/api/beta/suppliers", new CreateSupplierRequest(Guid.NewGuid(), new("Supplier Alpha", null, null, null, null, null)));
        Assert.Equal(HttpStatusCode.Created, supplierResponse.StatusCode);
        var supplier = (await supplierResponse.Content.ReadFromJsonAsync<SaveSupplierResponse>())!;
        // AND changing directory identity even with an unchanged display name needs the same explanation.
        var identityRejected = await SendAsync(client, HttpMethod.Put, url, new UpdateDraftOrderRequest(Guid.NewGuid(), saved.Version, saved.Draft with { SupplierId = supplier.SupplierId }));
        Assert.Equal(HttpStatusCode.BadRequest, identityRejected.StatusCode);
        Assert.True((await identityRejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("draft.charges[0].notes", out _));
        // WHEN replacing or clearing the supplier without new notes THEN the charge explains the required correction.
        foreach (var name in new string?[] { "Supplier Beta", null })
        {
            var rejected = await SendAsync(client, HttpMethod.Put, url, new UpdateDraftOrderRequest(Guid.NewGuid(), saved.Version, saved.Draft with { SupplierName = name }));
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.True((await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("draft.charges[0].notes", out _));
        }
        // WHEN the owner explains the replacement THEN it persists and an exact retry remains safe.
        var update = new UpdateDraftOrderRequest(Guid.NewGuid(), saved.Version, saved.Draft with { SupplierName = "Supplier Beta", Charges = [saved.Draft.Charges[0] with { Notes = "Original source; supplier replaced" }] });
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, url, update)).StatusCode);
        Assert.True((await (await SendAsync(client, HttpMethod.Put, url, update)).Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!.Replayed);
    }

    [Fact]
    public async Task AdjustmentsPersistCalculateAndProtectCorrectionsAndOlderReaders()
    {
        // GIVEN a current owner and merchandise with a confirmed supplier charge.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient(); await LoginAsync(client);
        var charge = new DraftCharge(Guid.NewGuid(), "shipping", "Freight", "15", "supplier", null, "confirmed", null, "Source quote");
        var draft = DraftOrderPricingTests.Empty with { Entries = [DraftOrderPricingTests.Line with { Quantity = "10", Price = "20", Discount = new("percentage", "10") }], OrderDiscount = new("fixed", "10"), Charges = [charge] };
        // WHEN calculated and saved THEN server estimates agree and adjustments round-trip.
        var calculation = await SendAsync(client, HttpMethod.Post, Path + "/calculate", new CalculateDraftOrderRequest(draft));
        Assert.Equal(HttpStatusCode.OK, calculation.StatusCode);
        Assert.Equal("185.0000", (await calculation.Content.ReadFromJsonAsync<DraftCalculationResponse>())!.PurchaseEstimate);
        var create = new CreateDraftOrderRequest(Guid.NewGuid(), draft);
        var saved = await SendAsync(client, HttpMethod.Post, Path, create);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var receipt = (await saved.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        var url = $"{Path}/{receipt.DraftOrderId}";
        var detail = (await client.GetFromJsonAsync<DraftOrderResponse>(url))!;
        Assert.Equal("185.0000", detail.Calculation.PurchaseEstimate);
        Assert.Equal("15.0000", detail.Draft.Charges[0].Amount);
        Assert.Equal("10.0000", detail.Draft.Entries[0].Discount!.Value);
        // AND an unchanged explanation cannot authorize another correction, but new notes can.
        var update = new UpdateDraftOrderRequest(Guid.NewGuid(), detail.Version, detail.Draft with { Charges = [detail.Draft.Charges[0] with { Amount = "16" }] });
        var rejected = await SendAsync(client, HttpMethod.Put, url, update);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.True((await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("draft.charges[0].notes", out _));
        update = update with { Draft = update.Draft with { Charges = [update.Draft.Charges[0] with { Notes = "Source quote; corrected carrier quote" }] } };
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, url, update)).StatusCode);
        Assert.True((await (await SendAsync(client, HttpMethod.Put, url, update)).Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!.Replayed);
        // AND older clients and other tenants cannot omit or access the saved financial content.
        foreach (var oldPath in new[] { "/api/purchase-order-drafts", "/api/v2/purchase-order-drafts", "/api/v3/purchase-order-drafts", "/api/v4/purchase-order-drafts" })
            Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync($"{oldPath}/{receipt.DraftOrderId}")).StatusCode);
        using var other = app.CreateClient(); await LoginAsync(other, "other@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(url)).StatusCode);
        var oldBody = JsonSerializer.SerializeToNode(new CreateDraftOrderRequest(Guid.NewGuid(), draft), DraftOrderInput.JsonOptions)!;
        oldBody["draft"]!.AsObject().Remove("charges"); oldBody["draft"]!.AsObject().Remove("orderDiscount");
        oldBody["draft"]!["entries"]![0]!.AsObject().Remove("discount");
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(client, HttpMethod.Post, Path, oldBody)).StatusCode);
        // AND a previously successful request identifier cannot bypass the current required fields.
        oldBody["requestId"] = create.RequestId;
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(client, HttpMethod.Post, Path, oldBody)).StatusCode);
    }
}
