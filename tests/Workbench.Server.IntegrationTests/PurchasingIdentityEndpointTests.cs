// Copyright (c) 2026 The White Stag Collection.
using System.Net;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed partial class PurchasingIdentityEndpointTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task V2DraftCanSaveAnEmptySupplierSnapshot()
    {
        // GIVEN an authenticated owner starting an incomplete purchase.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        await LoginAsync(client);
        // WHEN a V2 draft is saved with explicitly absent supplier details.
        var result = await SendAsync(client, HttpMethod.Post, "/api/v2/purchase-order-drafts", new
        {
            requestId = Guid.NewGuid(),
            draft = new
            {
                title = (string?)null,
                supplierName = (string?)null,
                currency = (string?)null,
                notes = (string?)null,
                sourceLinks = Array.Empty<string>(),
                entries = Array.Empty<object>(),
                supplierId = (Guid?)null,
                supplierContactName = (string?)null,
                supplierEmail = (string?)null,
                supplierPhone = (string?)null,
                supplierWebsite = (string?)null,
                supplierPostalAddress = (string?)null,
                supplierOrderReference = (string?)null,
                platform = (string?)null
            }
        });
        // THEN its first save creates a durable purchase.
        Assert.Equal(HttpStatusCode.Created, result.StatusCode);
    }
}
