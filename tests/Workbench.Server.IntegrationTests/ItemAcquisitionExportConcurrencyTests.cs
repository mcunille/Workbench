// Copyright (c) 2026 The White Stag Collection.

using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Inventory;
using Workbench.Server.Persistence;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ItemAcquisitionExportConcurrencyTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AcquisitionEditOrUnlinkWaitsForCoherentCsvAndPackageSnapshot(bool package, bool unlink)
    {
        // GIVEN saved acquisition facts and a reader paused before its export transaction commits.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        await using var storageFactory = ItemExportEndpointTests.CreateExportFactory(app);
        using var writer = storageFactory.CreateClient();
        await LoginAsync(writer);
        var item = await CreateItemAsync(writer);
        using var created = await SendAsync(writer, HttpMethod.Post, $"/api/items/{item.Id}/acquisition",
            new CreateAcquisitionRequest(Guid.NewGuid(), item.Version, "Gift", "Before acquisition", 2020, null, null, "Before notes"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var saved = (await created.Content.ReadFromJsonAsync<ItemAcquisitionResponse>())!;
        var barrier = new BeforeCommit();
        await using var factory = storageFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<WorkbenchDbContext>(options => options.AddInterceptors(barrier))));
        using var reader = factory.CreateClient();
        await LoginAsync(reader);
        var exporting = SendAsync(reader, HttpMethod.Post, ItemExportEndpointTests.ExportPath(package), new { scope = "all" });
        await barrier.Held.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // WHEN an independent SQL session edits or removes the captured relationship.
        var writing = unlink
            ? SendAsync(writer, HttpMethod.Put, $"/api/items/{item.Id}/acquisition-link",
                new LinkAcquisitionRequest(saved.ItemVersion, saved.Acquisition!.Id, saved.Acquisition.Version, null, null))
            : SendAsync(writer, HttpMethod.Put, $"/api/items/{item.Id}/acquisition/{saved.Acquisition!.Id}",
                new UpdateAcquisitionRequest(saved.ItemVersion, saved.Acquisition.Version, "Purchase", "After acquisition", 2021, 2, 3, "After notes"));
        try { await ItemExportConcurrencyTests.AssertBlockedWriterAsync(app.AdminConnectionString); }
        finally { barrier.Release.TrySetResult(); }
        using var response = await exporting;
        using var written = await writing;

        // THEN the export locks protected the relationship and facts together until capture completed.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        var csv = await ItemExportEndpointTests.ReadRecordsAsync(response, package);
        Assert.Contains(saved.Acquisition.Id.ToString("D"), csv);
        Assert.Contains("Before acquisition", csv);
        Assert.Contains("Before notes", csv);
        Assert.DoesNotContain("After acquisition", csv);
        Assert.DoesNotContain("After notes", csv);

        // AND a fresh snapshot reflects the later committed facts or the removed relationship.
        using var next = await SendAsync(writer, HttpMethod.Post, ItemExportEndpointTests.ExportPath(package), new { scope = "all" });
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        var nextCsv = await ItemExportEndpointTests.ReadRecordsAsync(next, package);
        Assert.DoesNotContain("Before acquisition", nextCsv);
        Assert.DoesNotContain("Before notes", nextCsv);
        if (unlink) Assert.DoesNotContain(saved.Acquisition.Id.ToString("D"), nextCsv);
        else { Assert.Contains("After acquisition", nextCsv); Assert.Contains("After notes", nextCsv); }
    }

    private sealed class BeforeCommit : DbTransactionInterceptor
    {
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Held.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            return result;
        }
    }
}
