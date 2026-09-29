// Copyright (c) 2026 The White Stag Collection.
using System.Net.Http.Json;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Purchasing;
using Workbench.Server.Storage;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class FinancialEvidenceEndpointTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task DisposalHttpBindsVersionsReplaysAndReauthorizesSavedOutcome()
    {
        // GIVEN eligible evidence read through the same authenticated document list used by the UI.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var uploaded = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        await FinancialEvidenceRemovalTests.PostAsync(context, uploaded);
        await FinancialEvidenceDisposalTests.ExpireAsync(context);
        await using var factory = context.Journal.Application.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IBlobStore>(); services.AddSingleton<IBlobStore>(storage.Store); }));
        using var client = factory.CreateClient(); await AcquisitionEndpointTests.LoginAsync(client, AuthTestApplication.AdminEmail);
        var path = $"/api/beta/purchase-orders/{context.Recognition.PurchaseOrderId}/documents";
        var list = (await client.GetFromJsonAsync<PurchaseOrderDocumentsResponse>(path))!;
        var document = Assert.Single(list.Documents);
        Assert.True(document.Retention!.CanDispose);
        var request = new DisposePurchaseOrderDocumentRequest(Guid.NewGuid(), list.OrderVersion, document.Version, document.Retention.EvidenceVersion!, "Retention completed");
        var route = $"{path}/{document.Id}/retention-disposals";
        // WHEN transport input or antiforgery is invalid THEN the endpoint cannot dispose.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route, request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AcquisitionEndpointTests.SendAsync(client, HttpMethod.Post, route, request with { ExpectedEvidenceVersion = "0x0102030405060708" })).StatusCode);
        var unknown = System.Text.Json.JsonSerializer.SerializeToNode(request, System.Text.Json.JsonSerializerOptions.Web)!.AsObject(); unknown["actorId"] = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await AcquisitionEndpointTests.SendAsync(client, HttpMethod.Post, route, unknown)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AcquisitionEndpointTests.SendAsync(client, HttpMethod.Post,
            $"{path}/{Guid.NewGuid()}/retention-disposals", request)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AcquisitionEndpointTests.SendAsync(client, HttpMethod.Post,
            $"/api/beta/purchase-orders/{Guid.NewGuid()}/documents/{document.Id}/retention-disposals", request)).StatusCode);
        // WHEN the exact authorized request succeeds and is retried after removal THEN the saved response is stable.
        var result = await AcquisitionEndpointTests.SendAsync(client, HttpMethod.Post, route, request);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var saved = (await result.Content.ReadFromJsonAsync<PurchaseOrderDocumentOperationResponse>())!;
        Assert.Equal(8, Convert.FromBase64String(saved.OrderVersion!).Length);
        Assert.Equal(saved, await (await AcquisitionEndpointTests.SendAsync(client, HttpMethod.Post, route, request)).Content.ReadFromJsonAsync<PurchaseOrderDocumentOperationResponse>());
        Assert.Equal(saved, await client.GetFromJsonAsync<PurchaseOrderDocumentOperationResponse>($"{path}/operations/{request.RequestId}"));
        Assert.Equal(HttpStatusCode.Conflict, (await AcquisitionEndpointTests.SendAsync(client, HttpMethod.Post, route, request with { Reason = "Changed" })).StatusCode);
        // AND ordinary PO access cannot recover the private disposal outcome after its accounting permission is lost.
        await context.AdminAsync("DELETE FROM [Identity].RoleClaims WHERE ClaimValue='AccountingConfigurationManage'");
        Assert.Equal(HttpStatusCode.Forbidden, (await AcquisitionEndpointTests.SendAsync(client, HttpMethod.Post, route, request)).StatusCode);
        var denied = await client.GetAsync($"{path}/operations/{request.RequestId}");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.True(denied.Headers.CacheControl?.NoStore);
        // AND the ordinary removal replay route cannot be used to bypass disposal authority.
        Assert.Equal(HttpStatusCode.Conflict, (await AcquisitionEndpointTests.SendAsync(client, HttpMethod.Delete, $"{path}/{document.Id}",
            new ChangePurchaseOrderDocumentRequest(request.RequestId, request.ExpectedOrderVersion, request.ExpectedDocumentVersion, null))).StatusCode);
        // AND restoring the role claim does not authorize a user whose role membership has been removed.
        await context.AdminAsync($"""
            INSERT [Identity].RoleClaims(TenantId,RoleId,ClaimType,ClaimValue)
              SELECT TenantId,RoleId,N'workbench/permission',N'AccountingConfigurationManage' FROM Administration.AccountingRoles WHERE Kind='Administrator';
            DELETE m FROM [Identity].UserRoles m JOIN Administration.AccountingRoles r ON r.TenantId=m.TenantId AND r.RoleId=m.RoleId
              WHERE m.UserId='{JournalTestContext.ActorId}';
            """);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{path}/operations/{request.RequestId}")).StatusCode);
        await context.AdminAsync($"UPDATE [Identity].Sessions SET RevokedAtUtc=SYSUTCDATETIME() WHERE UserId='{JournalTestContext.ActorId}'");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"{path}/operations/{request.RequestId}")).StatusCode);
    }

    [Theory]
    [InlineData("missing", HttpStatusCode.Gone)]
    [InlineData("corrupt", HttpStatusCode.Gone)]
    [InlineData("outage", HttpStatusCode.ServiceUnavailable)]
    public async Task DownloadDistinguishesUnavailableContentFromProviderFailure(string failure, HttpStatusCode expected)
    {
        // GIVEN real posted metadata and a provider with a specific read failure.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var uploaded = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        await FinancialEvidenceRemovalTests.PostAsync(context, uploaded);
        var faulty = new ReadFailureStore(storage.Store, failure);
        await using var factory = context.Journal.Application.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IBlobStore>(); services.AddSingleton<IBlobStore>(faulty); }));
        using var client = factory.CreateClient(); await AcquisitionEndpointTests.LoginAsync(client, AuthTestApplication.AdminEmail);
        // WHEN downloading THEN a digest failure or known missing bytes never returns substituted content.
        var result = await client.GetAsync($"/api/beta/purchase-orders/{context.Recognition.PurchaseOrderId}/documents/{uploaded.Document}/download");
        Assert.Equal(expected, result.StatusCode);
        Assert.True(result.Headers.CacheControl?.NoStore);
    }

    private sealed class ReadFailureStore(IBlobStore inner, string failure) : IBlobStore
    {
        public string Alias => inner.Alias;
        public Task<Stream> OpenReadAsync(BlobObjectId id, CancellationToken ct) => failure switch
        {
            "missing" => throw new FileNotFoundException(),
            "outage" => throw new IOException("Provider unavailable"),
            _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3]))
        };
        public Task<BlobContentIdentity> StageAsync(BlobObjectId id, Stream content, long max, CancellationToken ct) => inner.StageAsync(id, content, max, ct);
        public Task PublishAsync(BlobObjectId id, CancellationToken ct) => inner.PublishAsync(id, ct);
        public Task DeleteAsync(BlobObjectId id, CancellationToken ct) => inner.DeleteAsync(id, ct);
        public IAsyncEnumerable<BlobObjectId> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
        public Task CheckReadyAsync(CancellationToken ct) => inner.CheckReadyAsync(ct);
    }
    [Fact]
    public async Task AuthorizedBillReadDistinguishesEvidenceFromAvailableBytes()
    {
        // GIVEN posted bill evidence with durable original metadata.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var uploaded = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        await FinancialEvidenceRemovalTests.PostAsync(context, uploaded);
        var bill = await context.ScalarAsync<Guid>("SELECT OwnerId FROM Accounting.FinancialEvidenceSets");
        // WHEN the authenticated source read is projected THEN original evidence includes availability and policy.
        var json = await FinancialEvidenceDisposalTests.ExecuteAsync(context,
            $"EXEC Purchasing.ReadSupplierBill '{JournalTestContext.ActorId}','{context.Journal.SessionId}','{bill}'");
        using var result = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(result.RootElement.TryGetProperty("financialEvidence", out var evidence));
        Assert.Equal("Available", Assert.Single(evidence.GetProperty("links").EnumerateArray()).GetProperty("availability").GetString());
        using var client = context.Journal.Application.CreateClient();
        await AcquisitionEndpointTests.LoginAsync(client, AuthTestApplication.AdminEmail);
        var journalId = await context.ScalarAsync<Guid>("SELECT TOP(1) Id FROM Accounting.JournalEntries");
        var itemId = await context.ScalarAsync<Guid>("SELECT TOP(1) Id FROM Purchasing.SupplierOpenItems WHERE BillId IS NOT NULL");
        foreach (var route in new[] { $"/api/beta/accounting/journals/{journalId}", $"/api/beta/accounting/supplier-open-items/{itemId}" })
        {
            var response = await client.GetAsync(route); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(evidence.GetProperty("id").GetGuid(), body.RootElement.GetProperty("financialEvidence").GetProperty("id").GetGuid());
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        // AND recovery dispositions change availability without rewriting the original link.
        await context.AdminAsync($"INSERT Storage.RecoveryFiles(TenantId,RevisionId,ReportId,Generation,Reason,AcceptedAtUtc) VALUES('{JournalTestContext.TenantId}','{uploaded.Revision}',NEWID(),1,'Missing',SYSUTCDATETIME())");
        foreach (var state in new[] { "Missing", "Corrupt" })
        {
            await context.AdminAsync($"UPDATE Storage.RecoveryFiles SET Reason='{state}'");
            using var unavailable = System.Text.Json.JsonDocument.Parse(await FinancialEvidenceDisposalTests.ExecuteAsync(context,
                $"EXEC Purchasing.ReadSupplierBill '{JournalTestContext.ActorId}','{context.Journal.SessionId}','{bill}'"));
            Assert.Equal(state, Assert.Single(unavailable.RootElement.GetProperty("financialEvidence").GetProperty("links").EnumerateArray()).GetProperty("availability").GetString());
        }
        await FinancialEvidenceDisposalTests.ExpireAsync(context);
        await FinancialEvidenceDisposalTests.ExecuteAsync(context, await FinancialEvidenceDisposalTests.CommandAsync(context, uploaded.Document, Guid.NewGuid()));
        using var disposed = System.Text.Json.JsonDocument.Parse(await FinancialEvidenceDisposalTests.ExecuteAsync(context,
            $"EXEC Purchasing.ReadSupplierBill '{JournalTestContext.ActorId}','{context.Journal.SessionId}','{bill}'"));
        Assert.Equal("Disposed", Assert.Single(disposed.RootElement.GetProperty("financialEvidence").GetProperty("links").EnumerateArray()).GetProperty("availability").GetString());
    }
    [Fact]
    public async Task OrdinaryDocumentReadReportsRetentionWithoutFinancialSnapshots()
    {
        // GIVEN posted evidence and ordinary authenticated purchase access.
        await using var context = await SupplierBillPostingTests.OpenAsync(sqlServer);
        using var storage = new PurchaseOrderDocumentEndpointTests.TestStorage();
        var uploaded = await FinancialEvidencePostingTests.UploadAsync(context, storage);
        await FinancialEvidenceRemovalTests.PostAsync(context, uploaded);
        await using var factory = context.Journal.Application.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IBlobStore>(); services.AddSingleton<IBlobStore>(storage.Store); }));
        using var client = factory.CreateClient(); await AcquisitionEndpointTests.LoginAsync(client, AuthTestApplication.AdminEmail);
        // WHEN the ordinary document list is read THEN it explains indefinite protection without source snapshots.
        var response = await client.GetAsync($"/api/beta/purchase-orders/{context.Recognition.PurchaseOrderId}/documents");
        response.EnsureSuccessStatusCode();
        var document = Assert.Single((await response.Content.ReadFromJsonAsync<PurchaseOrderDocumentsResponse>())!.Documents);
        Assert.NotNull(document.Retention);
        Assert.True(document.Retention.Retained);
        Assert.True(document.Retention.Indefinite);
        Assert.False(document.Retention.CanDispose);
        Assert.Equal(8, Convert.FromBase64String(document.Retention.EvidenceVersion!).Length);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain("sourceSnapshot", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
}
