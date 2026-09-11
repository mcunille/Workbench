// Copyright (c) 2026 The White Stag Collection.
using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Authorization;
using Workbench.Server.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Inventory;
using Workbench.Server.Storage;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;
namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class AcquisitionDocumentInterruptionTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task AmbiguousPublicationStaysPendingAndExactRetryPublishesOnlyOnce()
    {
        // GIVEN provider publication succeeds but its confirmation is lost.
        using var store = new InterruptedStore();
        store.AfterPublish = () => throw new IOException("Injected lost provider response");
        await WithApplication(store, async (client, path, context, application) =>
        {
            var request = Guid.NewGuid(); var bytes = PhotoFixture.Png();
            // WHEN the request loses that response THEN no partial document is visible and its operation remains pending.
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await AcquisitionDocumentEndpointTests.UploadAsync(client, path, context, request, bytes)).StatusCode);
            Assert.Empty((await client.GetFromJsonAsync<AcquisitionDocumentsResponse>(path))!.Documents);
            Assert.Equal("Pending", (await client.GetFromJsonAsync<AcquisitionDocumentOperationResponse>($"{path}/operations/{request}"))!.State);
            // WHEN the identical request is retried THEN immutable publication is reconciled without another revision.
            Assert.Equal(HttpStatusCode.OK, (await AcquisitionDocumentEndpointTests.UploadAsync(client, path, context, request, bytes)).StatusCode);
            var document = Assert.Single((await client.GetFromJsonAsync<AcquisitionDocumentsResponse>(path))!.Documents);
            Assert.Equal(bytes, await client.GetByteArrayAsync($"{path}/{document.Id}/download"));
            Assert.Equal(1, await Count(application, "Inventory.AcquisitionDocumentOperations"));
            Assert.Equal(1, await Count(application, "Storage.Revisions"));
        });
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationLosingArchiveOrUnlinkRaceRetiresBytesAndNeverExposesDocument(bool unlink)
    {
        using var store = new InterruptedStore();
        await WithApplication(store, async (client, path, context, application) =>
        {
            // GIVEN a concurrent lifecycle change after provider publication but before final SQL exposure.
            store.AfterPublish = async () =>
            {
                await using var connection = new SqlConnection(application.AdminConnectionString); await connection.OpenAsync();
                await using var command = new SqlCommand(unlink ?
                    "DELETE Inventory.AcquisitionItems WHERE AcquisitionId=@id; UPDATE Inventory.Acquisitions SET Method=Method WHERE Id=@id;" :
                    "UPDATE Inventory.Items SET ArchivedAtUtc=SYSUTCDATETIME() WHERE Id IN(SELECT ItemId FROM Inventory.AcquisitionItems WHERE AcquisitionId=@id)", connection);
                command.Parameters.AddWithValue("@id", context.Acquisition!.Id); await command.ExecuteNonQueryAsync();
            };
            // WHEN finalization rechecks the relationship THEN it loses safely and schedules ordinary retention.
            Assert.Equal(HttpStatusCode.Conflict, (await AcquisitionDocumentEndpointTests.UploadAsync(client, path, context, Guid.NewGuid(), PhotoFixture.Png())).StatusCode);
            Assert.Equal(0, await Count(application, "Inventory.AcquisitionDocuments"));
            Assert.Equal(1, await Count(application, "Inventory.AcquisitionDocumentOperations WHERE State=2"));
            Assert.Equal(1, await Count(application, "Storage.Attachments WHERE DeletedAtUtc IS NOT NULL AND DeleteAfterUtc>SYSUTCDATETIME()"));
            Assert.Equal(1, await Count(application, "Operations.WorkItems WHERE Kind=1"));
        });
    }
    [Fact]
    public async Task IdenticalConcurrentRequestsSerializeAndReturnOneDurableResult()
    {
        using var store = new InterruptedStore();
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.AfterPublish = async () => { published.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(30)); };
        await WithApplication(store, async (client, path, context, application) =>
        {
            // GIVEN one request paused after publication while another independently reaches the same tenant UUID.
            var request = Guid.NewGuid(); var bytes = PhotoFixture.Png();
            var first = AcquisitionDocumentEndpointTests.UploadAsync(client, path, context, request, bytes);
            await published.Task.WaitAsync(TimeSpan.FromSeconds(30));
            try
            {
                // WHEN the second request competes THEN it receives retryable feedback without another reservation.
                Assert.Equal(HttpStatusCode.ServiceUnavailable, (await AcquisitionDocumentEndpointTests.UploadAsync(client, path, context, request, bytes)).StatusCode);
            }
            finally { release.TrySetResult(); }
            Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await AcquisitionDocumentEndpointTests.UploadAsync(client, path, context, request, bytes)).StatusCode);
            Assert.Single((await client.GetFromJsonAsync<AcquisitionDocumentsResponse>(path))!.Documents);
            Assert.Equal(1, await Count(application, "Inventory.AcquisitionDocumentOperations"));
        });
    }
    [Fact]
    public async Task IndependentServiceSessionsSerializeIdenticalRequestWithSqlApplicationLock()
    {
        using var store = new InterruptedStore();
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.AfterPublish = async () =>
        {
            published.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        };
        await WithApplication(store, async (_, _, context, application) =>
        {
            // GIVEN independently open restricted SQL sessions, bypassing HTTP/process admission entirely.
            var proof = application.Factory.Services.GetRequiredService<TenantContextProof>();
            await using var firstDatabase = BlobPersistenceTests.CreateContext(application.WebConnectionString, proof, AuthTestApplication.TenantId);
            await using var secondDatabase = BlobPersistenceTests.CreateContext(application.WebConnectionString, proof, AuthTestApplication.TenantId);
            await firstDatabase.Database.OpenConnectionAsync();
            await secondDatabase.Database.OpenConnectionAsync();
            var firstSession = await firstDatabase.Database.SqlQuery<int>($"SELECT CONVERT(int, @@SPID) AS Value").SingleAsync();
            var secondSession = await secondDatabase.Database.SqlQuery<int>($"SELECT CONVERT(int, @@SPID) AS Value").SingleAsync();
            Assert.NotEqual(firstSession, secondSession);
            var acquisitionId = context.Acquisition!.Id;
            var itemId = await firstDatabase.AcquisitionItems.Where(row => row.AcquisitionId == acquisitionId).Select(row => row.ItemId).SingleAsync();
            var actor = new RequestActor(AuthTestApplication.AdminUserId, AuthTestApplication.TenantId, Guid.NewGuid(), new HashSet<string>());
            var firstService = new AcquisitionDocumentService(firstDatabase, store, actor);
            var secondService = new AcquisitionDocumentService(secondDatabase, store, actor);
            var requestId = Guid.NewGuid();
            var bytes = PhotoFixture.Png();
            var itemVersion = Convert.FromBase64String(context.ItemVersion);
            var acquisitionVersion = Convert.FromBase64String(context.Acquisition.Version);
            Task<AcquisitionDocumentOperationResponse> Submit(AcquisitionDocumentService service) => service.ChangeAsync(
                itemId, acquisitionId, requestId, itemVersion, acquisitionVersion, null, null,
                "Concurrent receipt", bytes, new ValidatedDocument("image/png", "png"), default);
            var first = Submit(firstService);
            try
            {
                await published.Task.WaitAsync(TimeSpan.FromSeconds(30));
                // WHEN the second session competes while the first owns its application lock THEN SQL admission returns retryable feedback.
                var busy = await Assert.ThrowsAsync<DocumentInputException>(() => Submit(secondService));
                Assert.Equal(503, busy.StatusCode);
                Assert.Equal("This document operation is still running. Retry shortly.", busy.Message);
                // AND one reservation and pending revision exist, with no visible document.
                Assert.Equal(1, await secondDatabase.AcquisitionDocumentOperations.CountAsync(row => row.RequestId == requestId && row.State == 0));
                Assert.Equal(1, await secondDatabase.AttachmentRevisions.CountAsync(row => row.State == RevisionState.Pending));
                Assert.Empty(await secondDatabase.AcquisitionDocuments.ToArrayAsync());
            }
            finally { release.TrySetResult(); await first; }
            // WHEN publication completes and the independent session replays THEN both observe the same single durable outcome.
            var completed = await first;
            var replay = await Submit(secondService);
            Assert.Equal("Completed", completed.State);
            Assert.Equal(completed, replay);
            Assert.Equal(1, await secondDatabase.AcquisitionDocumentOperations.CountAsync(row => row.RequestId == requestId && row.State == 1));
            Assert.Single(await secondDatabase.AcquisitionDocuments.ToArrayAsync());
            Assert.Single(await secondDatabase.AttachmentRevisions.ToArrayAsync());
        });
    }
    [Fact]
    public async Task ConcurrentRenameAndRemovalHaveOneWinner()
    {
        using var store = new InterruptedStore();
        await WithApplication(store, async (client, path, context, application) =>
        {
            // GIVEN a saved document and two commands carrying the same three versions.
            Assert.Equal(HttpStatusCode.OK, (await AcquisitionDocumentEndpointTests.UploadAsync(client, path, context, Guid.NewGuid(), PhotoFixture.Png())).StatusCode);
            var listing = (await client.GetFromJsonAsync<AcquisitionDocumentsResponse>(path))!; var document = Assert.Single(listing.Documents);
            var rename = new ChangeAcquisitionDocumentRequest(Guid.NewGuid(), listing.ItemVersion, listing.AcquisitionVersion, document.Version, "Renamed receipt");
            var remove = rename with { RequestId = Guid.NewGuid(), Label = null };
            // WHEN independent requests race THEN only one can commit and the other reports stale state.
            var responses = await Task.WhenAll(SendAsync(client, HttpMethod.Put, $"{path}/{document.Id}", rename), SendAsync(client, HttpMethod.Delete, $"{path}/{document.Id}", remove));
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            var final = (await client.GetFromJsonAsync<AcquisitionDocumentsResponse>(path))!;
            Assert.True(final.Documents.Length == 0 || final.Documents[0].Label == "Renamed receipt");
        });
    }
    private async Task WithApplication(IBlobStore store, Func<HttpClient, string, ItemAcquisitionResponse, AuthTestApplication, Task> action)
    {
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        await using var factory = application.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        { services.RemoveAll<IBlobStore>(); services.AddSingleton(store); }));
        using var client = factory.CreateClient(); await LoginAsync(client);
        var item = await CreateItemAsync(client);
        using var created = await SendAsync(client, HttpMethod.Post, $"/api/items/{item.Id}/acquisition", new CreateAcquisitionRequest(Guid.NewGuid(), item.Version, "Gift", null, null, null, null, null));
        var context = (await created.Content.ReadFromJsonAsync<ItemAcquisitionResponse>())!;
        await action(client, $"/api/items/{item.Id}/acquisition/{context.Acquisition!.Id}/documents", context, application);
    }
    private static async Task<int> Count(AuthTestApplication application, string table)
    {
        await using var connection = new SqlConnection(application.AdminConnectionString); await connection.OpenAsync();
        await using var command = new SqlCommand($"SELECT COUNT(*) FROM {table}", connection); return (int)(await command.ExecuteScalarAsync())!;
    }
    private sealed class InterruptedStore : IBlobStore, IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "workbench-document-interruption-" + Guid.NewGuid().ToString("N"));
        private readonly FileSystemBlobStore inner;
        public InterruptedStore() { Directory.CreateDirectory(root); inner = new(root); }
        public Func<Task>? AfterPublish { get; set; }
        public string Alias => inner.Alias;
        public Task<BlobContentIdentity> StageAsync(BlobObjectId id, Stream content, long maximumBytes, CancellationToken ct) => inner.StageAsync(id, content, maximumBytes, ct);
        public async Task PublishAsync(BlobObjectId id, CancellationToken ct)
        { await inner.PublishAsync(id, ct); var callback = AfterPublish; AfterPublish = null; if (callback is not null) await callback(); }
        public Task<Stream> OpenReadAsync(BlobObjectId id, CancellationToken ct) => inner.OpenReadAsync(id, ct);
        public Task DeleteAsync(BlobObjectId id, CancellationToken ct) => inner.DeleteAsync(id, ct);
        public IAsyncEnumerable<BlobObjectId> ListAsync(CancellationToken ct) => inner.ListAsync(ct);
        public Task CheckReadyAsync(CancellationToken ct) => inner.CheckReadyAsync(ct);
        public void Dispose() => Directory.Delete(root, true);
    }
}
