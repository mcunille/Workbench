// Copyright (c) 2026 The White Stag Collection.

using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;
using static Workbench.Server.IntegrationTests.ItemExportEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ItemExportConcurrencyTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData("edit", "all", false)]
    [InlineData("archive", "all", false)]
    [InlineData("restore", "all", false)]
    [InlineData("insert", "all", false)]
    [InlineData("edit", "active", false)]
    [InlineData("archive", "active", false)]
    [InlineData("restore", "active", false)]
    [InlineData("insert", "active", false)]
    [InlineData("edit", "all", true)]
    [InlineData("archive", "all", true)]
    [InlineData("restore", "all", true)]
    [InlineData("insert", "all", true)]
    [InlineData("edit", "active", true)]
    [InlineData("archive", "active", true)]
    [InlineData("restore", "active", true)]
    [InlineData("insert", "active", true)]
    public async Task ExportIsOneSnapshotWhileAnIndependentSessionChangesTheCollection(string change, string scope, bool package)
    {
        // GIVEN a retained record and a second session whose export pauses with its snapshot locks held.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        await using var storageFactory = CreateExportFactory(app);
        using var writer = storageFactory.CreateClient();
        await LoginAsync(writer);
        var created = await PostAsync(writer, "/api/items", new { creationRequestId = Guid.NewGuid(), name = "Before snapshot" });
        var item = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = item.GetProperty("id").GetGuid();
        var version = item.GetProperty("version").GetString();
        if (change == "restore")
        {
            var archived = await PostAsync(writer, $"/api/items/{id}/archive", new { expectedVersion = version });
            version = (await archived.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString();
        }
        var barrier = new BeforeCommit();
        await using var factory = storageFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<WorkbenchDbContext>(options => options.AddInterceptors(barrier))));
        using var reader = factory.CreateClient();
        await LoginAsync(reader);
        var exporting = PostAsync(reader, ExportPath(package), new { scope });
        await barrier.Held.Task.WaitAsync(TimeSpan.FromSeconds(15));
        // WHEN an independent real HTTP/SQL session attempts a conflicting write.
        Task<HttpResponseMessage> writing = change switch
        {
            "insert" => PostAsync(writer, "/api/items", new { creationRequestId = Guid.NewGuid(), name = "After snapshot" }),
            "edit" => PutAsync(writer, $"/api/items/{id}", new { expectedVersion = version, name = "After snapshot" }),
            _ => PostAsync(writer, $"/api/items/{id}/{change}", new { expectedVersion = version }),
        };
        try
        {
            await AssertBlockedWriterAsync(app.AdminConnectionString);
        }
        finally { barrier.Release.TrySetResult(); }
        var response = await exporting;
        var written = await writing;
        // THEN SQL held the write until the snapshot was complete, and the file contains its prior state.
        Assert.Equal(scope == "active" && change == "restore" ? HttpStatusCode.NoContent : HttpStatusCode.OK, response.StatusCode);
        Assert.True(written.IsSuccessStatusCode);
        var csv = await ReadRecordsAsync(response, package);
        if (scope == "active" && change == "restore") Assert.Equal("", csv);
        else
        {
            Assert.Contains("Before snapshot", csv);
            Assert.Contains(change == "restore" ? "\"true\"" : "\"false\"", csv);
        }
        Assert.DoesNotContain("After snapshot", csv);
        // AND a new snapshot includes the later committed change.
        var next = await PostAsync(writer, ExportPath(package), new { scope });
        var nextCsv = await ReadRecordsAsync(next, package);
        Assert.Equal(scope == "active" && change == "archive" ? HttpStatusCode.NoContent : HttpStatusCode.OK, next.StatusCode);
        if (change is "insert" or "edit") Assert.Contains("After snapshot", nextCsv);
        else if (scope == "active" && change == "archive") Assert.Equal("", nextCsv);
        else Assert.Contains(change == "archive" ? "\"true\"" : "\"false\"", nextCsv);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionRevokedDuringPreparationCannotReceivePreparedBytes(bool package)
    {
        // GIVEN an authenticated member and preparation paused before commit.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        await using var storageFactory = CreateExportFactory(app);
        using var client = storageFactory.CreateClient();
        await LoginAsync(client);
        await PostAsync(client, "/api/items", new { creationRequestId = Guid.NewGuid(), name = "Private text" });
        var barrier = new BeforeCommit();
        await using var factory = storageFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<WorkbenchDbContext>(options => options.AddInterceptors(barrier))));
        using var reader = factory.CreateClient();
        await LoginAsync(reader);
        var exporting = PostAsync(reader, ExportPath(package), new { scope = "all" });
        await barrier.Held.Task.WaitAsync(TimeSpan.FromSeconds(15));
        // WHEN an administrator revokes the member's sessions while preparation is in flight.
        try
        {
            await using var sql = new SqlConnection(app.AdminConnectionString);
            await sql.OpenAsync();
            await using var revoke = new SqlCommand("UPDATE [Identity].[Sessions] SET RevokedAtUtc=SYSUTCDATETIME(), RevocationReason=N'test' WHERE UserId=@user", sql);
            revoke.Parameters.AddWithValue("@user", AuthTestApplication.MemberUserId);
            await revoke.ExecuteNonQueryAsync();
        }
        finally { barrier.Release.TrySetResult(); }
        // THEN the final authoritative check denies delivery of the already prepared file.
        var response = await exporting;
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.DoesNotContain("Private text", await response.Content.ReadAsStringAsync());
    }

    internal static async Task AssertBlockedWriterAsync(string connectionString)
    {
        await using var sql = new SqlConnection(connectionString);
        await sql.OpenAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var command = new SqlCommand("SELECT COUNT(*) FROM sys.dm_exec_requests WHERE database_id=DB_ID() AND blocking_session_id > 0 AND wait_type LIKE 'LCK%';", sql);
            if ((int)(await command.ExecuteScalarAsync(deadline.Token))! > 0) return;
            await Task.Delay(25, deadline.Token);
        }
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string path, object body)
    {
        var token = await client.GetFromJsonAsync<JsonElement>("/api/auth/antiforgery");
        using var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", token.GetProperty("requestToken").GetString());
        return await client.SendAsync(request);
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
