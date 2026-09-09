// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Inventory;
using Workbench.Server.Persistence;
using Xunit;
using static Workbench.Server.IntegrationTests.ItemExportEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ItemExportInterruptionTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BlockedSqlReadTerminatesAndReleasesPreparationCapacity(bool clientAborts, bool package)
    {
        // GIVEN an independent SQL transaction preventing the export from reading its collection.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        await using var storageFactory = CreateExportFactory(app);
        // Keep SQL's own timeout longer than the assertion window so it cannot mask a missing application deadline.
        await using var factory = storageFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<WorkbenchDbContext>(options => options.UseSqlServer(app.WebConnectionString,
                sql => sql.CommandTimeout(240)))));
        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(180);
        await LoginAsync(client);
        await using var blocker = new SqlConnection(app.AdminConnectionString);
        await blocker.OpenAsync();
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using var hold = new SqlCommand("SELECT COUNT(*) FROM [Inventory].[Items] WITH (TABLOCKX,HOLDLOCK)", blocker, transaction);
        await hold.ExecuteScalarAsync();
        using var cancellation = new CancellationTokenSource();
        var pending = PostAsync(client, ExportPath(package), new { scope = "all" }, cancellation.Token);
        try
        {
            await ItemExportConcurrencyTests.AssertBlockedWriterAsync(app.AdminConnectionString);
            // WHEN the client cancels, or the real 30-second CSV / 120-second package preparation deadline expires.
            if (clientAborts)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
            else
            {
                var response = await pending.WaitAsync(TimeSpan.FromSeconds(package ? 135 : 40));
                // THEN a timed-out request gives actionable failure and never an attachment.
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.Contains("export_preparation_failed", await response.Content.ReadAsStringAsync());
                Assert.Null(response.Content.Headers.ContentDisposition);
            }
            // THEN both slots recover while SQL is still blocked: cancellation must stop preparation,
            // rather than relying on releasing the blocker to let an abandoned request finish normally.
            var capacity = factory.Services.GetRequiredService<ItemExportCapacity>();
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var first = capacity.TryEnter();
                var second = first && capacity.TryEnter();
                if (first) capacity.Release();
                if (second) capacity.Release();
                if (second) break;
                await Task.Delay(25, cleanupDeadline.Token);
            }
        }
        finally { await transaction.RollbackAsync(); }
        // AND a new export can succeed when the database becomes available again.
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(client, ExportPath(package), new { scope = "all" })).StatusCode);
    }
}
