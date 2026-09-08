// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using Microsoft.Data.SqlClient;
using System.Net.Http.Json;
using System.Text.Json;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Inventory;
using Xunit;
using Xunit.Abstractions;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ItemRestorationTests(SqlServerFixture sqlServer, ITestOutputHelper output)
{
    [Fact]
    public async Task RestoreRetainsEditedIdentityAndCreationReplayAndChecksCycles()
    {
        // GIVEN an edited record archived after its original creation.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        await SendAsync(client, "/api/auth/login", new { email = "member@example.com", password = AuthTestApplication.AdminPassword });
        var creation = new { creationRequestId = Guid.NewGuid(), name = "Original" };
        var original = (await (await SendAsync(client, "/api/items", creation)).Content.ReadFromJsonAsync<ItemDetailResponse>())!;
        var token = await client.GetFromJsonAsync<JsonElement>("/api/auth/antiforgery");
        using var editRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/items/{original.Id}")
        { Content = JsonContent.Create(new { expectedVersion = original.Version, name = "Original edited", notes = "Retained", location = "Tray" }) };
        editRequest.Headers.Add("X-CSRF-TOKEN", token.GetProperty("requestToken").GetString());
        var edited = (await (await client.SendAsync(editRequest)).Content.ReadFromJsonAsync<ItemDetailResponse>())!;
        var archived = (await (await SendAsync(client, $"/api/items/{original.Id}/archive", new { expectedVersion = edited.Version })).Content.ReadFromJsonAsync<ItemDetailResponse>())!;
        Assert.Single((await client.GetFromJsonAsync<ItemPageResponse>("/api/items/archived?q=original"))!.Items);
        // WHEN restoring the displayed archived version.
        var response = await SendAsync(client, $"/api/items/{original.Id}/restore", new { expectedVersion = archived.Version });
        // THEN the same identity returns to active browsing with a new version.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var restored = (await response.Content.ReadFromJsonAsync<ItemDetailResponse>())!;
        Assert.Null(restored.ArchivedAtUtc);
        Assert.NotEqual(archived.Version, restored.Version);
        Assert.Equal(edited with { Version = restored.Version }, restored);
        Assert.Empty((await client.GetFromJsonAsync<ItemPageResponse>("/api/items/archived"))!.Items);
        Assert.Single((await client.GetFromJsonAsync<ItemPageResponse>("/api/items?q=original"))!.Items);
        Assert.Equal(restored, await (await SendAsync(client, "/api/items", creation)).Content.ReadFromJsonAsync<ItemDetailResponse>());
        // AND replay of a different creation payload remains a conflict after restoration.
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(client, "/api/items", new { creation.creationRequestId, name = "Different" })).StatusCode);
        // AND retry never attributes an active record to this request.
        var retry = await SendAsync(client, $"/api/items/{original.Id}/restore", new { expectedVersion = archived.Version });
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Equal("item_active", (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var rearchived = (await (await SendAsync(client, $"/api/items/{original.Id}/archive", new { expectedVersion = restored.Version })).Content.ReadFromJsonAsync<ItemDetailResponse>())!;
        var delayed = await SendAsync(client, $"/api/items/{original.Id}/restore", new { expectedVersion = archived.Version });
        Assert.Equal(HttpStatusCode.Conflict, delayed.StatusCode);
        Assert.Equal("item_version_conflict", (await delayed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(rearchived, await client.GetFromJsonAsync<ItemDetailResponse>($"/api/items/{original.Id}"));
    }

    [Fact]
    public async Task RestoreRejectsMalformedUnprotectedAndForeignRequests()
    {
        // GIVEN independent authenticated tenants and a saved record.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var owner = app.CreateClient();
        using var other = app.CreateClient();
        using var anonymous = app.CreateClient();
        await SendAsync(owner, "/api/auth/login", new { email = "member@example.com", password = AuthTestApplication.AdminPassword });
        await SendAsync(other, "/api/auth/login", new { email = "other@example.com", password = AuthTestApplication.AdminPassword });
        var item = (await (await SendAsync(owner, "/api/items", new { creationRequestId = Guid.NewGuid(), name = "Private" })).Content.ReadFromJsonAsync<ItemDetailResponse>())!;
        using var archive = await SendAsync(owner, $"/api/items/{item.Id}/archive", new { expectedVersion = item.Version });
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        Assert.True(archive.Headers.CacheControl?.Private);
        Assert.True(archive.Headers.CacheControl?.NoStore);
        item = (await archive.Content.ReadFromJsonAsync<ItemDetailResponse>())!;
        Assert.NotNull(item.ArchivedAtUtc);
        var path = $"/api/items/{item.Id}/restore";
        // WHEN invalid or unauthorized restoration requests arrive.
        foreach (var body in new object[] { new { }, new { expectedVersion = "bad" }, new { expectedVersion = "AQ==" }, new { expectedVersion = item.Version, name = "Injection" } })
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(owner, path, body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(path, new { expectedVersion = item.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, path, new { expectedVersion = item.Version })).StatusCode);
        // THEN missing and foreign records have indistinguishable outcomes.
        foreach (var id in new[] { item.Id, Guid.NewGuid() })
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(other, $"/api/items/{id}/restore", new { expectedVersion = item.Version })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/items/{item.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/items/archived")).StatusCode);
        using var archivedPage = await other.GetAsync("/api/items/archived");
        Assert.Equal(HttpStatusCode.OK, archivedPage.StatusCode);
        Assert.True(archivedPage.Headers.CacheControl?.Private);
        Assert.True(archivedPage.Headers.CacheControl?.NoStore);
        Assert.Empty((await archivedPage.Content.ReadFromJsonAsync<ItemPageResponse>())!.Items);
        foreach (var query in new[] { "cursor=bad", "q=" + new string('x', 201), "q=%00" })
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/items/archived?" + query)).StatusCode);
    }

    [Fact]
    public async Task ArchiveFiltersBeforePaginationWithLiteralSearch()
    {
        // GIVEN 56 matching records, with the earliest 51 archived through the checked command.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        await using var sql = new SqlConnection(app.AdminConnectionString);
        await sql.OpenAsync();
        await using var seed = new SqlCommand("""
            DECLARE @n int = 0, @id uniqueidentifier, @version binary(8);
            WHILE @n < 56
            BEGIN
                SET @id=NEWID();
                INSERT [Inventory].[Items] (Id,TenantId,TrackingKind,Name,CreatedAtUtc,CreationRequestId)
                    VALUES(@id,@tenant,'Individual',N'Pagination 100%_[] specimen',DATEADD(second,@n,'2026-01-01'),NEWID());
                IF @n < 51
                BEGIN
                    SELECT @version=RowVersion FROM [Inventory].[Items] WHERE Id=@id;
                    BEGIN TRANSACTION;
                    EXEC [Inventory].[ArchiveItem] @Id=@id,@ExpectedVersion=@version;
                    COMMIT;
                END;
                SET @n=@n+1;
            END;
            """, sql);
        seed.Parameters.AddWithValue("@tenant", AuthTestApplication.TenantId);
        await seed.ExecuteNonQueryAsync();
        using var client = app.CreateClient();
        await SendAsync(client, "/api/auth/login", new { email = "member@example.com", password = AuthTestApplication.AdminPassword });
        // WHEN ordinary and searched collection pages are traversed.
        foreach (var query in new[] { "", "q=100%25_%5B%5D&" })
        {
            var first = (await client.GetFromJsonAsync<ItemPageResponse>("/api/items/archived?" + query))!;
            // THEN the full first page and final page contain exactly the 51 archived identities.
            Assert.Equal(50, first.Items.Count);
            Assert.NotNull(first.NextCursor);
            var second = (await client.GetFromJsonAsync<ItemPageResponse>("/api/items/archived?" + query + "cursor=" + Uri.EscapeDataString(first.NextCursor)))!;
            Assert.Single(second.Items);
            Assert.Null(second.NextCursor);
            Assert.Equal(51, first.Items.Concat(second.Items).Select(row => row.Id).Distinct().Count());
            Assert.All(first.Items.Concat(second.Items), row => Assert.True(row.CreatedAtUtc.Second < 51));
        }
    }

    [Fact]
    public async Task ArchiveSearchKeepsLiteralCaseAccentAndTenantSemantics()
    {
        // GIVEN archived records with searchable names, notes, locations and literal punctuation.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var owner = app.CreateClient();
        using var foreign = app.CreateClient();
        await SendAsync(owner, "/api/auth/login", new { email = "member@example.com", password = AuthTestApplication.AdminPassword });
        await SendAsync(foreign, "/api/auth/login", new { email = "other@example.com", password = AuthTestApplication.AdminPassword });
        foreach (var client in new[] { owner, foreign })
        {
            var item = (await (await SendAsync(client, "/api/items", new { creationRequestId = Guid.NewGuid(), name = "Café specimen", notes = "100%_[] retained", location = "Tray A" })).Content.ReadFromJsonAsync<ItemDetailResponse>())!;
            await SendAsync(client, $"/api/items/{item.Id}/archive", new { expectedVersion = item.Version });
        }
        // WHEN the tenant searches any one field THEN literal matching preserves case/accent rules and isolation.
        foreach (var query in new[] { " CAFÉ ", "TRAY a", "%_[]", "retained" })
        {
            using var response = await owner.GetAsync("/api/items/archived?q=" + Uri.EscapeDataString(query));
            Assert.True(response.Headers.CacheControl?.Private);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Single((await response.Content.ReadFromJsonAsync<ItemPageResponse>())!.Items);
        }
        foreach (var query in new[] { "cafe", "absent", "' OR 1=1--", "specimen100" })
            Assert.Empty((await owner.GetFromJsonAsync<ItemPageResponse>("/api/items/archived?q=" + Uri.EscapeDataString(query)))!.Items);
    }
    [Fact]
    public async Task SparseArchiveQueriesRetainCorrectPagesAndRecordRepresentativeLatency()
    {
        // GIVEN 5,000 records with a sparse 100-record archive using the retained chronology index.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        await using var connection = new SqlConnection(app.AdminConnectionString);
        await connection.OpenAsync();
        await using (var seed = new SqlCommand("""
            INSERT [Inventory].[Items] ([Id],[TenantId],[TrackingKind],[Name],[CreatedAtUtc],[CreationRequestId])
                SELECT TOP (5000) NEWID(),@tenant,'Individual',N'Sparse specimen',
                    DATEADD(second,ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),'2026-01-01'),NEWID()
                FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            UPDATE [Inventory].[Items] SET [ArchivedAtUtc]=SYSUTCDATETIME()
                WHERE [TenantId]=@tenant AND DATEDIFF(second,'2026-01-01',[CreatedAtUtc]) % 50 = 0;
            """, connection))
        {
            seed.Parameters.AddWithValue("@tenant", AuthTestApplication.TenantId);
            await seed.ExecuteNonQueryAsync();
        }
        using var client = app.CreateClient();
        await SendAsync(client, "/api/auth/login", new { email = "member@example.com", password = AuthTestApplication.AdminPassword });
        // WHEN representative first, later, and no-match searches execute against real SQL.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var first = (await client.GetFromJsonAsync<ItemPageResponse>("/api/items/archived?q=specimen"))!;
        output.WriteLine("Sparse archive first page (includes initial query compilation): {0} ms", watch.Elapsed.TotalMilliseconds);
        watch.Restart();
        var second = (await client.GetFromJsonAsync<ItemPageResponse>("/api/items/archived?q=specimen&cursor=" + Uri.EscapeDataString(first.NextCursor!)))!;
        output.WriteLine("Sparse archive later page: {0} ms", watch.Elapsed.TotalMilliseconds);
        watch.Restart();
        var missing = (await client.GetFromJsonAsync<ItemPageResponse>("/api/items/archived?q=absent"))!;
        output.WriteLine("Sparse archive no matches: {0} ms", watch.Elapsed.TotalMilliseconds);
        // THEN the archive predicate precedes pagination and all 100 identities occur exactly once.
        Assert.Equal(50, first.Items.Count);
        Assert.Equal(50, second.Items.Count);
        Assert.Null(second.NextCursor);
        Assert.Equal(100, first.Items.Concat(second.Items).Select(row => row.Id).Distinct().Count());
        Assert.Empty(missing.Items);
        Assert.Null(missing.NextCursor);
    }
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, object body)
    {
        var token = await client.GetFromJsonAsync<JsonElement>("/api/auth/antiforgery");
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", token.GetProperty("requestToken").GetString());
        return await client.SendAsync(request);
    }
}
