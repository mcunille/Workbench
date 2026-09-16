// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
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
        var clock = new ExportClock();
        // Keep SQL's own timeout longer than the assertion window so it cannot mask a missing application deadline.
        await using var factory = storageFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
            services.AddDbContext<WorkbenchDbContext>(options => options.UseSqlServer(app.WebConnectionString,
                sql => sql.CommandTimeout(240)));
        }));
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
            // AND production still schedules the full 30-second CSV / 120-second package deadline.
            var expectedDeadline = TimeSpan.FromSeconds(package ? 120 : 30);
            Assert.Equal(expectedDeadline, Assert.Single(clock.Deadlines));
            // WHEN time advances to just before the deadline, only after SQL is observably blocked.
            clock.Advance(expectedDeadline - TimeSpan.FromMilliseconds(1));
            // THEN the deadline has not fired and the export remains pending.
            Assert.Equal(0, clock.DeadlinesFired);
            Assert.False(pending.IsCompleted);
            // WHEN the client cancels, or controlled time passes the preparation deadline.
            if (clientAborts)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            else
            {
                clock.Advance(TimeSpan.FromMilliseconds(2));
                Assert.Equal(1, clock.DeadlinesFired);
                using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
                // THEN a timed-out request gives actionable failure and never an attachment.
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.Contains("export_preparation_failed", await response.Content.ReadAsStringAsync());
                Assert.Contains(package ? "Retry a new snapshot." : "Retry to prepare a new snapshot.",
                    await response.Content.ReadAsStringAsync());
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
        finally
        {
            cancellation.Cancel();
            await transaction.RollbackAsync();
        }
        // AND a new export can succeed when the database becomes available again.
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(client, ExportPath(package), new { scope = "all" })).StatusCode);
    }

    private sealed class ExportClock() : FakeTimeProvider(DateTimeOffset.UtcNow)
    {
        public List<TimeSpan> Deadlines { get; } = [];
        public int DeadlinesFired { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Deadlines.Add(dueTime);
            return base.CreateTimer(value =>
            {
                DeadlinesFired++;
                callback(value);
            }, state, dueTime, period);
        }
    }
}
