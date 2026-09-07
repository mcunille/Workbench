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

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class InventorySearchTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task SearchMatchesLiteralContiguousTextInEachFieldWithExplicitCaseAndAccentSemantics()
    {
        // GIVEN searchable fields, null optional fields, literal punctuation, and unrelated objects.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        await LoginAsync(client, "member@example.com");
        await CreateAsync(client, "Blue stone", "collected in Paris", "Drawer A");
        await CreateAsync(client, "Café specimen", null, null);
        await CreateAsync(client, "100%_[]\\ specimen", "two  spaces", "Tray B");
        await CreateAsync(client, "Blue", "stone", null);

        // WHEN searching names, notes, and locations THEN a single field must contain the whole phrase.
        await AssertNamesAsync(client, "  BLUE stone \t", "Blue stone");
        await AssertNamesAsync(client, "PARIS", "Blue stone");
        await AssertNamesAsync(client, "drawer a", "Blue stone");
        await AssertNamesAsync(client, "cafe");
        await AssertNamesAsync(client, "CAFÉ", "Café specimen");
        await AssertNamesAsync(client, "absent");
        await AssertNamesAsync(client, "' OR 1=1--");
        await AssertNamesAsync(client, "two spaces");
        await AssertNamesAsync(client, "two  spaces", "100%_[]\\ specimen");
        foreach (var literal in new[] { "%", "_", "[", "]", "\\", "%_[]\\" })
            await AssertNamesAsync(client, literal, "100%_[]\\ specimen");
    }

    [Fact]
    public async Task SearchCancellationReachesDatabaseAndDatabaseFailureCanBeRetried()
    {
        // GIVEN a real SQL-backed search whose execution can be paused after request binding.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        var pause = new PausedSearch();
        await using var factory = application.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddDbContext<WorkbenchDbContext>(options => options.AddInterceptors(pause))));
        using var client = factory.CreateClient();
        await LoginAsync(client, "member@example.com");
        await CreateAsync(client, "Retry stone", null, null);

        // WHEN the caller cancels while SQL execution is pending.
        pause.Enabled = true;
        using var cancellation = new CancellationTokenSource();
        var pending = client.GetAsync(SearchUrl("Retry"), cancellation.Token);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();

        // THEN cancellation reaches the database operation and the same query can be retried.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await pause.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        pause.Enabled = false;
        await AssertNamesAsync(client, "Retry", "Retry stone");

        // WHEN SQL read authority is temporarily denied THEN search fails rather than reporting no matches.
        await using var connection = new SqlConnection(application.AdminConnectionString);
        await connection.OpenAsync();
        await using (var deny = new SqlCommand("DENY SELECT ON [Inventory].[Items] TO [workbench_web]", connection))
            await deny.ExecuteNonQueryAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, (await client.GetAsync(SearchUrl("Retry"))).StatusCode);

        // AND restoring authority allows an explicit retry without changing the query.
        await using (var grant = new SqlCommand("GRANT SELECT ON [Inventory].[Items] TO [workbench_web]", connection))
            await grant.ExecuteNonQueryAsync();
        await AssertNamesAsync(client, "Retry", "Retry stone");
    }

    [Fact]
    public async Task EmptyQueriesPreserveBrowsingAndNormalizedLimitsReturnPrivateProblemDetails()
    {
        // GIVEN an item containing exactly the maximum query length in its notes.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        await LoginAsync(client, "member@example.com");
        await CreateAsync(client, "Boundary", new string('x', 200), null);

        // WHEN the query is omitted, empty, whitespace-only, or at its normalized maximum.
        foreach (var query in new string?[] { null, "", " \t\u2003", new('x', 200), "  " + new string('x', 200) + "  " })
            await AssertNamesAsync(client, query, "Boundary");
        await AssertNamesAsync(client, string.Concat(Enumerable.Repeat("😀", 100)));

        // THEN oversized and NUL-containing queries are rejected without exposing private input.
        foreach (var query in new[] { new string('x', 201), string.Concat(Enumerable.Repeat("😀", 101)), "a\0b" })
        {
            using var response = await client.GetAsync(SearchUrl(query));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.True(response.Headers.CacheControl?.Private);
            Assert.Equal(400, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetInt32());
        }
        // AND cursor validation remains active during search.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(SearchUrl("Boundary") + "&cursor=invalid")).StatusCode);
    }

    [Fact]
    public async Task SearchFiltersBeforePagingAndKeepsTenantIsolationAcrossTiedTimestampPages()
    {
        // GIVEN 60 early nonmatches, 103 matching items sharing a timestamp, and foreign matching data.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        await using (var connection = new SqlConnection(application.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand("""
                DECLARE @i int = 0;
                WHILE @i < 163 BEGIN
                    INSERT [Inventory].[Items] ([Id], [TenantId], [TrackingKind], [Name], [CreatedAtUtc], [CreationRequestId])
                    VALUES (NEWID(), 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'Individual',
                        CASE WHEN @i < 60 THEN N'Unrelated' ELSE N'Matching stone' END,
                        CASE WHEN @i < 60 THEN '2025-01-01T00:00:00+00:00' ELSE '2026-01-01T00:00:00+00:00' END, NEWID());
                    SET @i += 1;
                END;
                INSERT [Inventory].[Items] ([Id], [TenantId], [TrackingKind], [Name], [CreatedAtUtc], [CreationRequestId])
                VALUES (NEWID(), 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'Individual', N'Matching foreign', '2026-01-01T00:00:00+00:00', NEWID());
                """, connection);
            await command.ExecuteNonQueryAsync();
        }
        using var client = application.CreateClient();
        await LoginAsync(client, "member@example.com");

        // WHEN traversing the matching subset, with the same query and existing cursor format.
        var ids = new List<Guid>();
        var counts = new List<int>();
        string? cursor = null;
        do
        {
            var page = await client.GetFromJsonAsync<JsonElement>(SearchUrl("MATCHING") +
                (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            var rows = page.GetProperty("items").EnumerateArray().ToList();
            counts.Add(rows.Count);
            Assert.All(rows, row => Assert.Equal("Matching stone", row.GetProperty("name").GetString()));
            ids.AddRange(rows.Select(row => row.GetProperty("id").GetGuid()));
            cursor = page.GetProperty("nextCursor").GetString();
        } while (cursor is not null && counts.Count < 5);

        // THEN all matches appear once in SQL UUID order, independently of earlier nonmatches or foreign data.
        Assert.Null(cursor);
        Assert.Equal(new[] { 50, 50, 3 }, counts);
        Assert.Equal(103, ids.Distinct().Count());
        Assert.Equal(ids.OrderBy(id => new System.Data.SqlTypes.SqlGuid(id)), ids);
        using var other = application.CreateClient();
        await LoginAsync(other, "other@example.com");
        await AssertNamesAsync(other, "matching", "Matching foreign");
        await AssertNamesAsync(other, "stone");
    }

    private static string SearchUrl(string? query) => "/api/items" + (query is null ? "" : "?q=" + Uri.EscapeDataString(query));

    private sealed class PausedSearch : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && command.CommandText.Contains("FROM [Inventory].[Items]", StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Cancelled.TrySetResult();
                    throw;
                }
            }
            return result;
        }
    }

    private static async Task AssertNamesAsync(HttpClient client, string? query, params string[] names)
    {
        var page = await client.GetFromJsonAsync<JsonElement>(SearchUrl(query));
        Assert.Equal(names, page.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("name").GetString()));
        Assert.Null(page.GetProperty("nextCursor").GetString());
    }

    private static async Task CreateAsync(HttpClient client, string name, string? notes, string? location)
    {
        using var response = await PostAsync(client, "/api/items", new { creationRequestId = Guid.NewGuid(), name, notes, location });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task LoginAsync(HttpClient client, string email)
    {
        using var response = await PostAsync(client, "/api/auth/login", new { email, password = AuthTestApplication.AdminPassword });
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
