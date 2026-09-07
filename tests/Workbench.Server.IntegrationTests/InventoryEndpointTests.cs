// Copyright (c) 2026 The White Stag Collection.

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Server.Persistence;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class InventoryEndpointTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task MemberCanCreateAndReopenNormalizedItemAndReplayWithoutDuplicates()
    {
        // GIVEN an authenticated ordinary tenant member and an unsaved individual object.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        await LoginAsync(client, "member@example.com");
        var request = new { creationRequestId = Guid.NewGuid(), name = "  Sapphire  ", notes = "First line\nSecond line", location = "  Tray A  " };

        // WHEN saving and replaying the same operation.
        var created = await PostAsync(client, "/api/items", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var detail = await created.Content.ReadFromJsonAsync<JsonElement>();
        var replay = await PostAsync(client, "/api/items", request);

        // THEN the normalized durable record is returned without creating a second item.
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("Sapphire", detail.GetProperty("name").GetString());
        Assert.Equal("Tray A", detail.GetProperty("location").GetString());
        Assert.Equal(request.notes, detail.GetProperty("notes").GetString());
        Assert.Equal(detail.GetProperty("id").GetGuid(), (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        using var nextSession = application.CreateClient();
        await LoginAsync(nextSession, "member@example.com");
        var reopened = await nextSession.GetFromJsonAsync<JsonElement>(created.Headers.Location);
        Assert.Equal(detail.GetProperty("id").GetGuid(), reopened.GetProperty("id").GetGuid());
        var list = await nextSession.GetFromJsonAsync<JsonElement>("/api/items");
        Assert.Single(list.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task ConcurrentIdenticalSavesProduceOneRecordAndDifferentReplayConflicts()
    {
        // GIVEN two authenticated sessions saving one draft concurrently.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        var barrier = new CompetingCreationReads();
        await using var factory = application.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<WorkbenchDbContext>(options => options.AddInterceptors(barrier))));
        using var first = factory.CreateClient();
        using var second = factory.CreateClient();
        await LoginAsync(first, "member@example.com");
        await LoginAsync(second, "member@example.com");
        var request = new { creationRequestId = Guid.NewGuid(), name = "Ring" };
        // WHEN independent HTTP requests compete for its creation identifier.
        var results = await Task.WhenAll(PostAsync(first, "/api/items", request), PostAsync(second, "/api/items", request));
        // THEN exactly one creates and the other replays the same durable identity.
        Assert.Single(results, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(results, response => response.StatusCode == HttpStatusCode.OK);
        var firstId = (await results[0].Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(firstId, (await results[1].Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        var conflict = await PostAsync(first, "/api/items", new { request.creationRequestId, name = "Different ring" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        // AND a different operation can intentionally create an identically named object.
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(first, "/api/items", new { creationRequestId = Guid.NewGuid(), name = "Ring" })).StatusCode);
        var list = await first.GetFromJsonAsync<JsonElement>("/api/items");
        Assert.Equal(2, list.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task ForeignItemsAreIndistinguishableFromMissingAndRequestIdsAreTenantScoped()
    {
        // GIVEN independent tenant sessions and one shared creation request identifier.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var first = application.CreateClient();
        using var other = application.CreateClient();
        await LoginAsync(first, "member@example.com");
        await LoginAsync(other, "other@example.com");
        var request = new { creationRequestId = Guid.NewGuid(), name = "Private stone" };
        var created = await PostAsync(first, "/api/items", request);
        // WHEN the other tenant addresses that item or browses its collection.
        var foreign = await other.GetAsync(created.Headers.Location);
        var missing = await other.GetAsync($"/api/items/{Guid.NewGuid()}");
        // THEN neither exposes the foreign record and the other tenant can reuse its own request identity.
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(missing.StatusCode, foreign.StatusCode);
        Assert.Empty((await other.GetFromJsonAsync<JsonElement>("/api/items")).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(other, "/api/items", request)).StatusCode);
    }

    [Fact]
    public async Task AuthenticationAntiforgeryAndServerValidationProtectCreation()
    {
        // GIVEN an anonymous client followed by an authenticated member.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/items")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/items/{Guid.NewGuid()}")).StatusCode);
        await LoginAsync(client, "member@example.com");
        // WHEN missing CSRF, invalid fields, or server-owned fields are submitted.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/items", new { creationRequestId = Guid.NewGuid(), name = "Ring" })).StatusCode);
        foreach (var body in new object[]
        {
            new { creationRequestId = Guid.Empty, name = "Ring" },
            new { creationRequestId = Guid.NewGuid(), name = " \t\u2003" },
            new { creationRequestId = Guid.NewGuid(), name = new string('x', 201) },
            new { creationRequestId = Guid.NewGuid(), name = "Ring", notes = new string('x', 4001) },
            new { creationRequestId = Guid.NewGuid(), name = "Ring", location = new string('x', 201) },
            new { creationRequestId = Guid.NewGuid(), name = "Ring", tenantId = Guid.NewGuid() },
            new { creationRequestId = Guid.NewGuid(), name = "Ring", trackingKind = "Lot" },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, "/api/items", body)).StatusCode);
        // THEN no invalid request creates data and responses prohibit private caching.
        var response = await client.GetAsync("/api/items");
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.Empty((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/items?cursor=invalid")).StatusCode);
    }

    [Fact]
    public async Task CursorTraversesAllTiedTimestampsUsingSqlUuidOrder()
    {
        // GIVEN 103 items with identical timestamps and deliberately nonsequential UUIDs.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(application.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = new Microsoft.Data.SqlClient.SqlCommand("""
                DECLARE @i int = 0;
                WHILE @i < 103 BEGIN
                    INSERT [Inventory].[Items] ([Id], [TenantId], [TrackingKind], [Name], [CreatedAtUtc], [CreationRequestId])
                    VALUES (NEWID(), 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'Individual', N'Ring', '2026-01-01T00:00:00+00:00', NEWID());
                    SET @i += 1;
                END;
                """, connection);
            await seed.ExecuteNonQueryAsync();
        }
        using var client = application.CreateClient();
        await LoginAsync(client, "member@example.com");
        // WHEN following each bounded page through the final cursor.
        var ids = new List<Guid>();
        string? cursor = null;
        var counts = new List<int>();
        do
        {
            var page = await client.GetFromJsonAsync<JsonElement>("/api/items" + (cursor is null ? "" : "?cursor=" + Uri.EscapeDataString(cursor)));
            var rows = page.GetProperty("items").EnumerateArray().ToList();
            counts.Add(rows.Count);
            ids.AddRange(rows.Select(row => row.GetProperty("id").GetGuid()));
            cursor = page.GetProperty("nextCursor").GetString();
        } while (cursor is not null && counts.Count < 5);
        // THEN every record appears once in the same order used by SQL Server.
        Assert.Equal(new[] { 50, 50, 3 }, counts);
        Assert.Equal(103, ids.Distinct().Count());
        Assert.Equal(ids.OrderBy(id => new System.Data.SqlTypes.SqlGuid(id)), ids);
    }
    [Fact]
    public async Task DatabaseFailureNeverReportsSuccessAndSameDraftCanBeRetried()
    {
        // GIVEN a saved draft whose database write authority becomes unavailable.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        await LoginAsync(client, "member@example.com");
        var request = new { creationRequestId = Guid.NewGuid(), name = "Retry stone" };
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(application.AdminConnectionString);
        await connection.OpenAsync();
        await using var deny = new Microsoft.Data.SqlClient.SqlCommand("DENY INSERT ON [Inventory].[Items] TO [workbench_web]", connection);
        await deny.ExecuteNonQueryAsync();
        // WHEN persistence fails THEN the response is an error and no record has been accepted.
        Assert.Equal(HttpStatusCode.InternalServerError, (await PostAsync(client, "/api/items", request)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/items")).GetProperty("items").EnumerateArray());
        // WHEN the database recovers and the frozen draft is explicitly retried.
        await using var grant = new Microsoft.Data.SqlClient.SqlCommand("GRANT INSERT ON [Inventory].[Items] TO [workbench_web]", connection);
        await grant.ExecuteNonQueryAsync();
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(client, "/api/items", request)).StatusCode);
        // THEN repeating the same uncertain save remains duplicate-safe.
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "/api/items", request)).StatusCode);
        Assert.Single((await client.GetFromJsonAsync<JsonElement>("/api/items")).GetProperty("items").EnumerateArray());
    }

    private sealed class CompetingCreationReads : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readCount;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM [Inventory].[Items]", StringComparison.Ordinal) &&
                command.CommandText.Contains("[CreationRequestId] =", StringComparison.Ordinal))
            {
                // Both real SQL reads must finish before either request may insert. This forces
                // the database unique-index race, rather than merely testing sequential replay.
                if (Interlocked.Increment(ref _readCount) == 2)
                    _bothRead.TrySetResult();
                await _bothRead.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }
    private static async Task LoginAsync(HttpClient client, string email)
    {
        var response = await PostAsync(client, "/api/auth/login", new { email, password = AuthTestApplication.AdminPassword });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body)
    {
        var token = await client.GetFromJsonAsync<JsonElement>("/api/auth/antiforgery");
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", token.GetProperty("requestToken").GetString());
        return await client.SendAsync(request);
    }
}
