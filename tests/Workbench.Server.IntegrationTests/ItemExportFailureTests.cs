// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Inventory;
using Xunit;
using static Workbench.Server.IntegrationTests.ItemExportEndpointTests;

namespace Workbench.Server.IntegrationTests;

public sealed class ItemExportBoundsTests
{
    [Fact]
    public void CsvAcceptsRowBoundaryAndRejectsOverflowAndCancellation()
    {
        // GIVEN the largest supported number of short records.
        var item = new ExportItem(Guid.NewGuid(), "Individual", "=stone", null, null, DateTimeOffset.UtcNow, null);
        var items = Enumerable.Repeat(item, ItemExportCsv.MaximumRows).ToArray();
        // WHEN encoding at the row boundary, one beyond it, or after cancellation.
        var bytes = ItemExportCsv.Encode(items, "active", DateTimeOffset.UtcNow, CancellationToken.None);
        // THEN no boundary record is dropped and excess rows or cancelled output are rejected.
        Assert.True(bytes.Length < ItemExportCsv.MaximumBytes);
        Assert.Throws<ItemExportLimitException>(() => ItemExportCsv.Encode([.. items, item], "all", DateTimeOffset.UtcNow, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ItemExportCsv.Encode(items, "active", DateTimeOffset.UtcNow, cancelled.Token));
    }

    [Fact]
    public void EncodedByteLimitIncludesMultibyteTextAndNeverReturnsTruncatedCsv()
    {
        // GIVEN valid maximum-length notes whose UTF-8 encoding exceeds the file bound before the row bound.
        var item = new ExportItem(Guid.NewGuid(), "Individual", "stone", new string('蓝', 4000), null, DateTimeOffset.UtcNow, null);
        // WHEN encoding the selected collection.
        // THEN exceeding encoded bytes fails instead of truncating rows or counting characters as bytes.
        Assert.Throws<ItemExportLimitException>(() => ItemExportCsv.Encode(Enumerable.Repeat(item, 3000).ToArray(), "all", DateTimeOffset.UtcNow, CancellationToken.None));
    }
}

[Collection(SqlServerCollection.Name)]
public sealed class ItemExportFailureTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task OversizedCollectionNeverProducesAPartialFile()
    {
        // GIVEN a tenant with one more record than the supported limit.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        await using var sql = new SqlConnection(app.AdminConnectionString);
        await sql.OpenAsync();
        await using var seed = new SqlCommand("""
            INSERT INTO [Inventory].[Items] (Id,TenantId,TrackingKind,Name,CreatedAtUtc,CreationRequestId)
            SELECT TOP (10001) NEWID(),@tenant,'Individual',N'Bounded record',SYSUTCDATETIME(),NEWID()
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            """, sql);
        seed.Parameters.AddWithValue("@tenant", AuthTestApplication.TenantId);
        await seed.ExecuteNonQueryAsync();
        using var client = app.CreateClient();
        await LoginAsync(client);
        // WHEN requesting the oversized scope.
        var response = await PostAsync(client, "/api/items/export", new { scope = "all" });
        // THEN no partial file is delivered and the limit is actionable.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("export_limit_exceeded", await response.Content.ReadAsStringAsync());
        Assert.Null(response.Content.Headers.ContentDisposition);
    }

    [Fact]
    public async Task BusyCapacityAndDatabaseFailureReleaseCapacityForRetry()
    {
        // GIVEN both preparation slots already in use.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        await LoginAsync(client);
        var capacity = app.Factory.Services.GetRequiredService<ItemExportCapacity>();
        Assert.True(capacity.TryEnter());
        Assert.True(capacity.TryEnter());
        try
        {
            // WHEN another preparation arrives THEN it receives retry guidance without a file.
            var busy = await PostAsync(client, "/api/items/export", new { scope = "all" });
            Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(5), busy.Headers.RetryAfter?.Delta);
            Assert.Null(busy.Content.Headers.ContentDisposition);
        }
        finally { capacity.Release(); capacity.Release(); }
        // GIVEN a database read failure during subsequent preparation.
        await using var sql = new SqlConnection(app.AdminConnectionString);
        await sql.OpenAsync();
        await using var deny = new SqlCommand("DENY SELECT ON [Inventory].[Items] TO [workbench_web]", sql);
        await deny.ExecuteNonQueryAsync();
        // WHEN repeated preparations fail THEN each returns a truthful error and releases its slot.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var failed = await PostAsync(client, "/api/items/export", new { scope = "all" });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.Contains("export_preparation_failed", await failed.Content.ReadAsStringAsync());
            Assert.Null(failed.Content.Headers.ContentDisposition);
        }
        await using var grant = new SqlCommand("GRANT SELECT ON [Inventory].[Items] TO [workbench_web]", sql);
        await grant.ExecuteNonQueryAsync();
        // THEN a fresh retry can prepare normally after recovery.
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(client, "/api/items/export", new { scope = "all" })).StatusCode);
    }
}
