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
        // GIVEN a supplier with a website and custom reference handles.
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
            ["socialProfiles"] = new JsonArray(
                new JsonObject { ["label"] = " Discord ", ["handle"] = " @example " },
                new JsonObject { ["label"] = "Community", ["handle"] = "someone (primary)" },
                new JsonObject { ["label"] = "Other", ["handle"] = "javascript:reference" })
        };
        var request = new { requestId = Guid.NewGuid(), supplier = content };
        // WHEN creating and retrying THEN all profiles survive normalization and the receipt replays.
        var response = await SendAsync(client, HttpMethod.Post, "/api/beta/suppliers", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = (await response.Content.ReadFromJsonAsync<SaveSupplierResponse>())!;
        Assert.True((await (await SendAsync(client, HttpMethod.Post, "/api/beta/suppliers", request)).Content.ReadFromJsonAsync<SaveSupplierResponse>())!.Replayed);
        var path = $"/api/beta/suppliers/{receipt.SupplierId}";
        var read = (await client.GetFromJsonAsync<JsonObject>(path))!;
        var profiles = (JsonArray)read["supplier"]!["socialProfiles"]!;
        Assert.Equal("Discord", profiles[0]!["label"]!.GetValue<string>());
        Assert.Equal("@example", profiles[0]!["handle"]!.GetValue<string>());
        Assert.Equal("someone (primary)", profiles[1]!["handle"]!.GetValue<string>());
        Assert.Equal("javascript:reference", profiles[2]!["handle"]!.GetValue<string>());
        // WHEN changing one reference, removing another, and retaining the website THEN values remain independent.
        content["socialProfiles"] = new JsonArray(
            new JsonObject { ["label"] = "Discord", ["handle"] = "@changed" },
            new JsonObject { ["label"] = "Other", ["handle"] = "javascript:reference" });
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Post, "/api/beta/suppliers", request)).StatusCode);
        var update = new { requestId = Guid.NewGuid(), expectedVersion = receipt.SavedVersion, supplier = content };
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, path, update)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, HttpMethod.Put, path,
            new { requestId = Guid.NewGuid(), expectedVersion = receipt.SavedVersion, supplier = content })).StatusCode);
        read = (await client.GetFromJsonAsync<JsonObject>(path))!;
        profiles = (JsonArray)read["supplier"]!["socialProfiles"]!;
        Assert.Equal(2, profiles.Count);
        Assert.Equal("@changed", profiles[0]!["handle"]!.GetValue<string>());
        Assert.Equal("Other", profiles[1]!["label"]!.GetValue<string>());
        Assert.Equal("https://example.test", read["supplier"]!["website"]!.GetValue<string>());
        // WHEN removing all references THEN the nullable field is omitted on the resulting read.
        content["socialProfiles"] = new JsonArray();
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Put, path,
            new { requestId = Guid.NewGuid(), expectedVersion = read["version"]!.GetValue<string>(), supplier = content })).StatusCode);
        read = (await client.GetFromJsonAsync<JsonObject>(path))!;
        Assert.Null(read["supplier"]!["socialProfiles"]);
    }
}
