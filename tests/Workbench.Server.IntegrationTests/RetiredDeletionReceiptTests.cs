// Copyright (c) 2026 The White Stag Collection.
using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Purchasing;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class RetiredDeletionReceiptTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task HistoricalDeletionWithPlusInVersionReplaysItsOriginalReceipt()
    {
        // GIVEN a retained tombstone and a historical SQL deletion fingerprint containing literal '+'.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        await LoginAsync(client);
        var created = await SendAsync(client, HttpMethod.Post, "/api/beta/purchase-order-drafts",
            new CreateDraftOrderRequest(Guid.NewGuid(), DraftOrderInputV4Tests.Empty));
        var saved = (await created.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        var deleted = await SendAsync(client, HttpMethod.Delete, $"/api/beta/purchase-order-drafts/{saved.DraftOrderId}",
            new DeleteDraftOrderRequest(Guid.NewGuid(), saved.SavedVersion));
        var deletion = (await deleted.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        var requestId = Guid.NewGuid();
        const string expectedVersion = "AAAAAAAA+AA=";
        await using (var connection = new SqlConnection(application.AdminConnectionString))
        {
            await connection.OpenAsync();
            // Seed the immutable historical evidence using the original SQL envelope, independently of the adapter.
            await using var seed = new SqlCommand("""
                DECLARE @Canonical nvarchar(max)=N'{"operation":"Delete","targetId":"'+LOWER(CONVERT(nvarchar(36),@draft))
                  +N'","expectedVersion":"AAAAAAAA+AA=","draft":null}';
                INSERT Purchasing.DraftOrderRequestReceipts
                  (TenantId,RequestId,DraftOrderId,Operation,ActorUserId,ExpectedRowVersion,FingerprintVersion,InputFingerprint,ResultRowVersion,CompletedAtUtc)
                VALUES(@tenant,@request,@draft,'Delete',@actor,@expected,1,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Canonical)),@result,SYSUTCDATETIME());
                """, connection);
            seed.Parameters.AddWithValue("@tenant", AuthTestApplication.TenantId);
            seed.Parameters.AddWithValue("@request", requestId);
            seed.Parameters.AddWithValue("@draft", saved.DraftOrderId);
            seed.Parameters.AddWithValue("@actor", AuthTestApplication.MemberUserId);
            seed.Parameters.AddWithValue("@expected", Convert.FromBase64String(expectedVersion));
            seed.Parameters.AddWithValue("@result", Convert.FromBase64String(deletion.SavedVersion));
            await seed.ExecuteNonQueryAsync();
        }
        // WHEN a browser repeats that exact deletion through each retired route.
        foreach (var version in new[] { "", "/v2", "/v3", "/v4" })
        {
            var response = await SendAsync(client, HttpMethod.Delete, $"/api{version}/purchase-order-drafts/{saved.DraftOrderId}",
                new DeleteDraftOrderRequest(requestId, expectedVersion));
            // THEN the original result is recovered rather than rejected as different input.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var receipt = (await response.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
            Assert.True(receipt.Replayed);
            Assert.Equal(requestId, receipt.RequestId);
            Assert.Equal(deletion.SavedVersion, receipt.SavedVersion);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/beta/purchase-order-drafts/{saved.DraftOrderId}")).StatusCode);
    }
}
