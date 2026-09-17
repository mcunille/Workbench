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
public sealed class DraftOrderEndpointV4Tests(SqlServerFixture sqlServer)
{
    private const string Path = "/api/v4/purchase-order-drafts";
    [Fact]
    public async Task AdjustmentsPersistCalculateAndProtectCorrectionsAndOlderReaders()
    {
        // GIVEN a current owner and merchandise with a confirmed supplier charge.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient(); await LoginAsync(client);
        var charge = new DraftCharge(Guid.NewGuid(), "shipping", "Freight", "15", "supplier", null, "confirmed", null, "Source quote");
        var draft = DraftOrderInputV4Tests.Empty with { Entries = [DraftOrderInputV4Tests.Line with { Quantity = "10", Price = "20", Discount = new("percentage", "10") }], OrderDiscount = new("fixed", "10"), Charges = [charge] };
        // WHEN calculated and saved THEN server estimates agree and adjustments round-trip.
        var calculation = await SendAsync(client, HttpMethod.Post, Path + "/calculate", new CalculateDraftOrderRequestV4(draft));
        Assert.Equal(HttpStatusCode.OK, calculation.StatusCode);
        Assert.Equal("185.0000", (await calculation.Content.ReadFromJsonAsync<DraftCalculationResponseV4>())!.PurchaseEstimate);
        var create = new CreateDraftOrderRequestV4(Guid.NewGuid(), draft);
        var saved = await SendAsync(client, HttpMethod.Post, Path, create);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var receipt = (await saved.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        var url = $"{Path}/{receipt.DraftOrderId}";
        var detail = (await client.GetFromJsonAsync<DraftOrderResponseV4>(url))!;
        Assert.Equal("185.0000", detail.Calculation.PurchaseEstimate);
        Assert.Equal("15.0000", detail.Draft.Charges[0].Amount);
        Assert.Equal("10.0000", detail.Draft.Entries[0].Discount!.Value);
        // AND an unchanged explanation cannot authorize another correction, but new notes can.
        var update = new UpdateDraftOrderRequestV4(Guid.NewGuid(), detail.Version, detail.Draft with { Charges = [detail.Draft.Charges[0] with { Amount = "16" }] });
        var rejected = await SendAsync(client, HttpMethod.Put, url, update);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.True((await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty("draft.charges[0].notes", out _));
        update = update with { Draft = update.Draft with { Charges = [update.Draft.Charges[0] with { Notes = "Source quote; corrected carrier quote" }] } };
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, url, update)).StatusCode);
        Assert.True((await (await SendAsync(client, HttpMethod.Put, url, update)).Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!.Replayed);
        // AND older clients and other tenants cannot omit or access the saved financial content.
        foreach (var oldPath in new[] { "/api/purchase-order-drafts", "/api/v2/purchase-order-drafts", "/api/v3/purchase-order-drafts" })
            Assert.Equal(HttpStatusCode.UpgradeRequired, (await client.GetAsync($"{oldPath}/{receipt.DraftOrderId}")).StatusCode);
        using var other = app.CreateClient(); await LoginAsync(other, "other@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(url)).StatusCode);
        var oldBody = JsonSerializer.SerializeToNode(new CreateDraftOrderRequestV4(Guid.NewGuid(), draft), DraftOrderInput.JsonOptions)!;
        oldBody["draft"]!.AsObject().Remove("charges"); oldBody["draft"]!.AsObject().Remove("orderDiscount");
        oldBody["draft"]!["entries"]![0]!.AsObject().Remove("discount");
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(client, HttpMethod.Post, Path, oldBody)).StatusCode);
    }
}
