// Copyright (c) 2026 The White Stag Collection.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Purchasing;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;

namespace Workbench.Server.IntegrationTests;

public sealed partial class PurchasingIdentityEndpointTests
{
    [Fact]
    public async Task SupplierProfilesPersistIndependentlyAndParticipateInRetryAndConcurrency()
    {
        // GIVEN a supplier with a website and all three optional profiles.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        await LoginAsync(client);
        var content = new JsonObject
        {
            ["name"] = "Profile supplier",
            ["contactName"] = null,
            ["email"] = null,
            ["phone"] = null,
            ["website"] = "https://example.test",
            ["postalAddress"] = null,
            ["instagram"] = " https://www.instagram.com/example/ ",
            ["x"] = "https://x.com/example",
            ["gemRockAuctions"] = "https://www.gemrockauctions.com/stores/example"
        };
        var request = new { requestId = Guid.NewGuid(), supplier = content };
        // WHEN creating and retrying THEN all profiles survive normalization and the receipt replays.
        var response = await SendAsync(client, HttpMethod.Post, "/api/beta/suppliers", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = (await response.Content.ReadFromJsonAsync<SaveSupplierResponse>())!;
        Assert.True((await (await SendAsync(client, HttpMethod.Post, "/api/beta/suppliers", request)).Content.ReadFromJsonAsync<SaveSupplierResponse>())!.Replayed);
        var path = $"/api/beta/suppliers/{receipt.SupplierId}";
        var read = (await client.GetFromJsonAsync<JsonObject>(path))!;
        Assert.Equal("https://www.instagram.com/example/", read["supplier"]!["instagram"]!.GetValue<string>());
        Assert.Equal(content["x"]!.GetValue<string>(), read["supplier"]!["x"]!.GetValue<string>());
        Assert.Equal(content["gemRockAuctions"]!.GetValue<string>(), read["supplier"]!["gemRockAuctions"]!.GetValue<string>());
        // WHEN changing one profile, removing another, and retaining the website THEN the new values are independent.
        content["instagram"] = "https://www.instagram.com/changed/";
        content["x"] = " ";
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Post, "/api/beta/suppliers", request)).StatusCode);
        var update = new { requestId = Guid.NewGuid(), expectedVersion = receipt.SavedVersion, supplier = content };
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, path, update)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Put, path,
            new { requestId = Guid.NewGuid(), expectedVersion = receipt.SavedVersion, supplier = content })).StatusCode);
        read = (await client.GetFromJsonAsync<JsonObject>(path))!;
        Assert.Equal(content["instagram"]!.GetValue<string>(), read["supplier"]!["instagram"]!.GetValue<string>());
        Assert.Null(read["supplier"]!["x"]);
        Assert.Equal("https://example.test", read["supplier"]!["website"]!.GetValue<string>());
        Assert.Equal(content["gemRockAuctions"]!.GetValue<string>(), read["supplier"]!["gemRockAuctions"]!.GetValue<string>());
    }
}
