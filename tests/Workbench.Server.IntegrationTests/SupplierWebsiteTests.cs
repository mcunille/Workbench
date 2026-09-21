// Copyright (c) 2026 The White Stag Collection.
using System.Net;
using System.Net.Http.Json;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Purchasing;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;

namespace Workbench.Server.IntegrationTests;

public sealed partial class PurchasingIdentityEndpointTests
{
    [Fact]
    public async Task SupplierWebsiteCreateAndUpdateNormalizeBeforePersistenceAndRejectInvalidInput()
    {
        // GIVEN an authenticated owner creating a supplier with a scheme-less website.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        await LoginAsync(client);
        var saved = await SupplierSave(client, Contact with { Website = " example.com/shop?q=one#two " });
        var path = $"/api/beta/suppliers/{saved.SupplierId}";
        var created = (await client.GetFromJsonAsync<SupplierResponse>(path))!;
        Assert.Equal("https://example.com/shop?q=one#two", created.Supplier.Website);

        // WHEN editing through the API THEN the normalized address survives a fresh read.
        var update = await SendAsync(client, HttpMethod.Put, path,
            new UpdateSupplierRequest(Guid.NewGuid(), saved.SavedVersion, Contact with { Website = " www.example.com/new?x=1#stock " }));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var edited = (await client.GetFromJsonAsync<SupplierResponse>(path))!;
        Assert.Equal("https://www.example.com/new?x=1#stock", edited.Supplier.Website);

        // AND bypassing the form cannot save unsupported schemes on either write endpoint.
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put })
        {
            object request = method == HttpMethod.Post
                ? new CreateSupplierRequest(Guid.NewGuid(), Contact with { Website = "javascript:alert(1)" })
                : new UpdateSupplierRequest(Guid.NewGuid(), edited.Version, Contact with { Website = "ftp://example.com" });
            var rejected = await SendAsync(client, method, method == HttpMethod.Post ? "/api/beta/suppliers" : path, request);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Contains("supplier.website", await rejected.Content.ReadAsStringAsync());
        }
        Assert.Equal(edited, await client.GetFromJsonAsync<SupplierResponse>(path));
    }
}
