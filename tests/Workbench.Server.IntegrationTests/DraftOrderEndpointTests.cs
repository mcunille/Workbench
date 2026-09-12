// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Purchasing;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class DraftOrderEndpointTests(SqlServerFixture sqlServer)
{
    private const string Path = "/api/purchase-order-drafts";
    private static DraftContent Empty => new(null, null, null, null, [], []);

    [Fact]
    public async Task EmptyDraftCanBeSavedAndResumedInAnotherSession()
    {
        // GIVEN an authenticated owner with an incomplete shopping list.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        await AcquisitionEndpointTests.LoginAsync(client);
        var request = new
        {
            requestId = Guid.NewGuid(),
            draft = new
            {
                title = (string?)null,
                supplierName = (string?)null,
                currency = (string?)null,
                notes = (string?)null,
                sourceLinks = Array.Empty<string>(),
                entries = Array.Empty<object>()
            }
        };
        // WHEN the empty draft is explicitly saved.
        var response = await AcquisitionEndpointTests.SendAsync(client, HttpMethod.Post, "/api/purchase-order-drafts", request);
        // THEN its receipt locates persistent content in a later authenticated session.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(receipt.GetProperty("replayed").GetBoolean());
        using var later = application.CreateClient();
        await AcquisitionEndpointTests.LoginAsync(later);
        var read = await later.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.True(read.Headers.CacheControl?.NoStore);
        var detail = await read.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(receipt.GetProperty("savedVersion").GetString(), detail.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("draft").GetProperty("title").ValueKind);
        Assert.Equal(0, detail.GetProperty("draft").GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task AuthorizationAntiforgeryAndStrictBindingProtectPrivateDrafts()
    {
        // GIVEN an anonymous browser and then an authenticated owner.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        var anonymous = await client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.True(anonymous.Headers.CacheControl?.NoStore);
        await LoginAsync(client);
        var request = new CreateDraftOrderRequest(Guid.NewGuid(), Empty);
        // WHEN CSRF or required/nested fields are invalid THEN no draft is created.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Path, request)).StatusCode);
        foreach (var invalid in new object[]
        {
            new { requestId = Guid.NewGuid(), draft = new { sourceLinks = Array.Empty<string>(), entries = Array.Empty<object>() } },
            new { requestId = Guid.NewGuid(), draft = new { title = (string?)null, supplierName = (string?)null, currency = (string?)null,
                notes = (string?)null, sourceLinks = Array.Empty<string>(), entries = new[] { new { id = Guid.NewGuid(), description = (string?)null,
                notes = (string?)null, sourceLink = (string?)null, indicativePrice = (string?)null, tenantId = Guid.NewGuid() } } } },
            new { requestId = Guid.NewGuid(), draft = (object?)null },
            new { requestId = Guid.NewGuid(), draft = new { title = (string?)null, supplierName = (string?)null, currency = "USD",
                notes = (string?)null, sourceLinks = Array.Empty<string>(), entries = new[] { new { id = Guid.NewGuid(), description = (string?)null,
                notes = (string?)null, sourceLink = (string?)null, indicativePrice = 1 } } } },
            request with { RequestId = Guid.Empty },
            request with { Draft = Empty with { SourceLinks = null! } },
            request with { Draft = Empty with { Entries = [null!] } },
            request with { Draft = Empty with { SourceLinks = [null!] } },
        })
        {
            var response = await SendAsync(client, HttpMethod.Post, Path, invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        Assert.Empty((await client.GetFromJsonAsync<DraftOrderPageResponse>(Path))!.Items);
    }

    [Fact]
    public async Task CompactReceiptReplaysAfterLaterEditsAndRejectsChangedBusinessInput()
    {
        // GIVEN a normalized priced draft and its original receipt.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        await LoginAsync(client);
        var request = new CreateDraftOrderRequest(Guid.NewGuid(), Empty with
        {
            Title = "  Plan ",
            Currency = "usd",
            SourceLinks = [" https://example.com/cart "],
            Entries = [new(Guid.NewGuid(), "  Stone ", " note\n ", null, "0"),
                new(Guid.NewGuid(), "Unknown price", null, "https://example.com/stone", null)]
        });
        var response = await SendAsync(client, HttpMethod.Post, Path, request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var first = (await response.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        var detailPath = $"{Path}/{first.DraftOrderId}";
        var original = (await client.GetFromJsonAsync<DraftOrderResponse>(detailPath))!;
        Assert.Equal("Plan", original.Draft.Title);
        Assert.Equal("USD", original.Draft.Currency);
        Assert.Equal("0.0000", original.Draft.Entries[0].IndicativePrice);
        Assert.Null(original.Draft.Entries[1].IndicativePrice);
        Assert.Equal("https://example.com/stone", original.Draft.Entries[1].SourceLink);
        Assert.Equal(" note\n ", original.Draft.Entries[0].Notes);
        var edit = new UpdateDraftOrderRequest(Guid.NewGuid(), original.Version, original.Draft with { Notes = "Later" });
        // WHEN a later save changes the current document and the original creation is retried.
        var updated = await SendAsync(client, HttpMethod.Put, detailPath, edit);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var later = (await updated.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        var replayResponse = await SendAsync(client, HttpMethod.Post, Path, request with { Draft = original.Draft });
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replay = (await replayResponse.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        // THEN its original compact evidence is stable and current content remains the later edit.
        Assert.Equal(first with { Replayed = true }, replay);
        Assert.Equal(detailPath, replayResponse.Headers.Location?.OriginalString);
        var current = (await client.GetFromJsonAsync<DraftOrderResponse>(detailPath))!;
        Assert.Equal("Later", current.Draft.Notes);
        Assert.Equal(later.SavedVersion, current.Version);
        Assert.NotEqual(replay.SavedVersion, current.Version);
        Assert.Equal("draft_request_conflict", await Code(await SendAsync(client, HttpMethod.Post, Path, request with { Draft = original.Draft with { Title = "plan" } }), HttpStatusCode.Conflict));
        Assert.Equal("draft_version_conflict", await Code(await SendAsync(client, HttpMethod.Put, detailPath, edit with { RequestId = Guid.NewGuid() }), HttpStatusCode.Conflict));
        Assert.Equal("draft_request_conflict", await Code(await SendAsync(client, HttpMethod.Put, detailPath, edit with { ExpectedVersion = current.Version }), HttpStatusCode.Conflict));
        // AND a save creates no collection, acquisition or financial side effect.
        await using var sql = new SqlConnection(application.AdminConnectionString);
        await sql.OpenAsync();
        foreach (var table in new[] { "Items", "Acquisitions", "AcquisitionItems" })
        {
            await using var count = new SqlCommand($"SELECT COUNT(*) FROM Inventory.{table}", sql);
            Assert.Equal(0, await count.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task CurrencyTransitionRequiresClearingSaveAndSuccessfulRetryIgnoresLaterState()
    {
        // GIVEN an already priced USD draft.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        await LoginAsync(client);
        var content = Empty with { Currency = "USD", Entries = [new(Guid.NewGuid(), null, null, null, "1.25")] };
        var saved = await Create(client, content);
        var path = $"{Path}/{saved.DraftOrderId}";
        var change = new UpdateDraftOrderRequest(Guid.NewGuid(), saved.SavedVersion, content with { Currency = "EUR" });
        // WHEN an amount is relabeled without a successful clearing save THEN it has a currency error.
        Assert.Equal("draft_validation_failed", await Code(await SendAsync(client, HttpMethod.Put, path, change), HttpStatusCode.BadRequest));
        var clear = change with { Draft = change.Draft with { Entries = [content.Entries[0] with { IndicativePrice = null }] } };
        var clearedResponse = await SendAsync(client, HttpMethod.Put, path, clear);
        Assert.Equal(HttpStatusCode.OK, clearedResponse.StatusCode);
        var cleared = (await clearedResponse.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        var pricedResponse = await SendAsync(client, HttpMethod.Put, path, change with { RequestId = Guid.NewGuid(), ExpectedVersion = cleared.SavedVersion });
        Assert.Equal(HttpStatusCode.OK, pricedResponse.StatusCode);
        // THEN re-entering the amount in a later save succeeds and the original clearing receipt still resolves.
        var replay = await SendAsync(client, HttpMethod.Put, path, clear);
        Assert.Equal(cleared with { Replayed = true }, await replay.Content.ReadFromJsonAsync<SaveDraftOrderResponse>());
        Assert.Equal("1.2500", (await client.GetFromJsonAsync<DraftOrderResponse>(path))!.Draft.Entries[0].IndicativePrice);
    }

    [Fact]
    public async Task ForeignIdentifiersAreIndistinguishableAndRequestIdsRemainTenantScoped()
    {
        // GIVEN independent business sessions, each using the same client request UUID.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var owner = application.CreateClient();
        using var other = application.CreateClient();
        await LoginAsync(owner);
        await LoginAsync(other, "other@example.com");
        var request = new CreateDraftOrderRequest(Guid.NewGuid(), Empty);
        var created = await SendAsync(owner, HttpMethod.Post, Path, request);
        var saved = (await created.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        // WHEN a foreign or missing target is requested THEN reads/writes expose no conflict details.
        foreach (var id in new[] { saved.DraftOrderId, Guid.NewGuid() })
        {
            Assert.Equal("draft_not_found", await Code(await other.GetAsync($"{Path}/{id}"), HttpStatusCode.NotFound));
            Assert.Equal("draft_not_found", await Code(await SendAsync(other, HttpMethod.Put, $"{Path}/{id}",
                new UpdateDraftOrderRequest(request.RequestId, saved.SavedVersion, Empty)), HttpStatusCode.NotFound));
        }
        Assert.Empty((await other.GetFromJsonAsync<DraftOrderPageResponse>(Path))!.Items);
        var otherCreated = await SendAsync(other, HttpMethod.Post, Path, request);
        Assert.Equal(HttpStatusCode.Created, otherCreated.StatusCode);
        Assert.NotEqual(saved.DraftOrderId, (await otherCreated.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!.DraftOrderId);
    }

    [Fact]
    public async Task ConcurrentRetriesCommitOneDraftAndCompetingVersionsHaveOneWinner()
    {
        // GIVEN two authorized sessions issuing the same save concurrently.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var first = application.CreateClient();
        using var second = application.CreateClient();
        await LoginAsync(first);
        await LoginAsync(second);
        var create = new CreateDraftOrderRequest(Guid.NewGuid(), Empty);
        // WHEN the matching requests race THEN exactly one creation and one replay identify the same draft.
        var created = await Task.WhenAll(SendAsync(first, HttpMethod.Post, Path, create), SendAsync(second, HttpMethod.Post, Path, create));
        Assert.Single(created, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(created, response => response.StatusCode == HttpStatusCode.OK);
        var saved = (await created[0].Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        var other = (await created[1].Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        Assert.Equal(saved.DraftOrderId, other.DraftOrderId);
        Assert.Equal(saved.SavedVersion, other.SavedVersion);
        var edit = new UpdateDraftOrderRequest(Guid.NewGuid(), saved.SavedVersion, Empty with { Notes = "A" });
        // WHEN different requests race on the same version THEN the loser cannot overwrite the winner.
        var edited = await Task.WhenAll(SendAsync(first, HttpMethod.Put, $"{Path}/{saved.DraftOrderId}", edit),
            SendAsync(second, HttpMethod.Put, $"{Path}/{saved.DraftOrderId}", edit with { RequestId = Guid.NewGuid(), Draft = Empty with { Notes = "B" } }));
        Assert.Single(edited, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(edited, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Single((await first.GetFromJsonAsync<DraftOrderPageResponse>(Path))!.Items);
    }

    [Fact]
    public async Task ForwardPagesUseNativeSqlUuidOrderingAndExactTimestampKeys()
    {
        // GIVEN 101 drafts with one exact timestamp, to exercise the UUID tiebreaker.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        await LoginAsync(client);
        Assert.Null((await client.GetFromJsonAsync<DraftOrderPageResponse>(Path))!.NextCursor);
        for (var index = 0; index < 101; index++)
        {
            await Create(client, Empty with { Title = $"Draft {index}" });
            if (index == 49)
            {
                var exactlyFifty = (await client.GetFromJsonAsync<DraftOrderPageResponse>(Path))!;
                Assert.Equal(50, exactlyFifty.Items.Count);
                Assert.Null(exactlyFifty.NextCursor);
            }
            if (index == 50) Assert.NotNull((await client.GetFromJsonAsync<DraftOrderPageResponse>(Path))!.NextCursor);
        }
        await using var sql = new SqlConnection(application.AdminConnectionString);
        await sql.OpenAsync();
        await using (var align = new SqlCommand("UPDATE Purchasing.DraftOrders SET UpdatedAtUtc='2099-09-12T02:00:00.1234567+00:00'", sql))
            await align.ExecuteNonQueryAsync();
        var expected = new List<Guid>();
        await using (var ordered = new SqlCommand("SELECT Id FROM Purchasing.DraftOrders ORDER BY UpdatedAtUtc DESC, Id DESC", sql))
        await using (var reader = await ordered.ExecuteReaderAsync())
            while (await reader.ReadAsync()) expected.Add(reader.GetGuid(0));
        // WHEN traversing every page THEN no UUID is skipped or repeated and cursor precision is retained.
        var ids = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = (await client.GetFromJsonAsync<DraftOrderPageResponse>(Path + (cursor is null ? "" : "?cursor=" + Uri.EscapeDataString(cursor))))!;
            ids.AddRange(page.Items.Select(row => row.Id));
            cursor = page.NextCursor;
            if (cursor is not null) Assert.Contains(".1234567", cursor);
        } while (cursor is not null);
        Assert.Equal(expected, ids);
        Assert.Equal(101, ids.Distinct().Count());
        // AND a draft moved above an existing cursor is discovered by refresh, not promised by a live continuation.
        var beforeMove = (await client.GetFromJsonAsync<DraftOrderPageResponse>(Path))!;
        await using (var move = new SqlCommand("UPDATE Purchasing.DraftOrders SET UpdatedAtUtc='2100-09-12T02:00:00.1234567+00:00' WHERE Id=@id", sql))
        {
            move.Parameters.AddWithValue("@id", expected[^1]);
            await move.ExecuteNonQueryAsync();
        }
        var continued = (await client.GetFromJsonAsync<DraftOrderPageResponse>(Path + "?cursor=" + Uri.EscapeDataString(beforeMove.NextCursor!)))!;
        Assert.DoesNotContain(continued.Items, row => row.Id == expected[^1]);
        Assert.Equal(expected[^1], (await client.GetFromJsonAsync<DraftOrderPageResponse>(Path))!.Items[0].Id);
        Assert.Equal("invalid_cursor", await Code(await client.GetAsync(Path + "?cursor=v2_bad"), HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task OversizedBodiesAreRejectedWithAndWithoutContentLength()
    {
        // GIVEN authenticated, CSRF-valid requests just beyond the byte limit.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        await LoginAsync(client);
        var token = await client.GetFromJsonAsync<JsonElement>("/api/auth/antiforgery");
        foreach (var chunked in new[] { false, true })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Path)
            { Content = new StreamContent(new MemoryStream(new byte[DraftOrderRequestMiddleware.MaximumBodyBytes + 1])) };
            request.Content.Headers.ContentType = new("application/json");
            request.Headers.Add("X-CSRF-TOKEN", token.GetProperty("requestToken").GetString());
            if (chunked) request.Headers.TransferEncodingChunked = true;
            // WHEN binding would exceed 4 MiB THEN the bounded admission returns 413 and keeps responses private.
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    [Fact]
    public async Task DeleteRemovesDraftFromReadsAndPagesAndReplaysItsOriginalReceipt()
    {
        // GIVEN a saved private shopping list and the version deliberately selected for deletion.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        await LoginAsync(client);
        var saved = await Create(client, Empty with { Title = "Remove this draft" });
        var path = $"{Path}/{saved.DraftOrderId}";
        var request = new { requestId = Guid.NewGuid(), expectedVersion = saved.SavedVersion };
        // WHEN the owner deletes that saved version.
        var deleted = await SendAsync(client, HttpMethod.Delete, path, request);
        // THEN the receipt confirms a new version, and reads and listing no longer expose the draft.
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.True(deleted.Headers.CacheControl?.NoStore);
        var receipt = (await deleted.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        Assert.Equal(request.requestId, receipt.RequestId);
        Assert.Equal(saved.DraftOrderId, receipt.DraftOrderId);
        Assert.False(receipt.Replayed);
        Assert.NotEqual(saved.SavedVersion, receipt.SavedVersion);
        Assert.Equal("draft_not_found", await Code(await client.GetAsync(path), HttpStatusCode.NotFound));
        Assert.Empty((await client.GetFromJsonAsync<DraftOrderPageResponse>(Path))!.Items);
        var cursor = DraftOrderCursor.Encode(DateTimeOffset.Parse("2100-01-01T00:00:00+00:00"), Guid.NewGuid());
        Assert.Empty((await client.GetFromJsonAsync<DraftOrderPageResponse>(Path + "?cursor=" + Uri.EscapeDataString(cursor)))!.Items);
        // AND an uncertain original retry returns its stable receipt without another mutation.
        var replay = await SendAsync(client, HttpMethod.Delete, path, request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt with { Replayed = true }, await replay.Content.ReadFromJsonAsync<SaveDraftOrderResponse>());
        Assert.Equal("draft_request_conflict", await Code(await SendAsync(client, HttpMethod.Delete, path,
            new { request.requestId, expectedVersion = receipt.SavedVersion }), HttpStatusCode.Conflict));
        Assert.Equal("draft_not_found", await Code(await SendAsync(client, HttpMethod.Delete, path,
            new { requestId = Guid.NewGuid(), request.expectedVersion }), HttpStatusCode.NotFound));
        Assert.Equal("draft_not_found", await Code(await SendAsync(client, HttpMethod.Put, path,
            new UpdateDraftOrderRequest(Guid.NewGuid(), receipt.SavedVersion, Empty)), HttpStatusCode.NotFound));
        // AND replaying an older creation receipt cannot recreate the deleted draft.
        var creationReplay = await SendAsync(client, HttpMethod.Post, Path,
            new CreateDraftOrderRequest(saved.RequestId, Empty with { Title = "Remove this draft" }));
        Assert.Equal(HttpStatusCode.OK, creationReplay.StatusCode);
        Assert.Equal(saved with { Replayed = true }, await creationReplay.Content.ReadFromJsonAsync<SaveDraftOrderResponse>());
        Assert.Equal("draft_not_found", await Code(await client.GetAsync(path), HttpStatusCode.NotFound));
        // AND receipt possession does not authorize a replay after the original actor loses access.
        await using var admin = new SqlConnection(application.AdminConnectionString);
        await admin.OpenAsync();
        await using (var disable = new SqlCommand("UPDATE [Identity].[Users] SET State=2 WHERE Id=@id", admin))
        {
            disable.Parameters.AddWithValue("@id", AuthTestApplication.MemberUserId);
            await disable.ExecuteNonQueryAsync();
        }
        var revoked = await SendAsync(client, HttpMethod.Delete, path, request);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.True(revoked.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task DeleteProtectsCurrentVersionsAuthorityAndAntiforgery()
    {
        // GIVEN an anonymous browser and an owner whose saved draft was subsequently edited.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var owner = application.CreateClient();
        var anonymous = await owner.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{Path}/{Guid.NewGuid()}")
        { Content = JsonContent.Create(new { requestId = Guid.NewGuid(), expectedVersion = Convert.ToBase64String(new byte[8]) }) });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.True(anonymous.Headers.CacheControl?.NoStore);
        await LoginAsync(owner);
        var saved = await Create(owner, Empty);
        var path = $"{Path}/{saved.DraftOrderId}";
        var edit = await SendAsync(owner, HttpMethod.Put, path,
            new UpdateDraftOrderRequest(Guid.NewGuid(), saved.SavedVersion, Empty with { Notes = "Keep newer work" }));
        var current = (await edit.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        // WHEN stale, malformed or CSRF-invalid requests attempt deletion THEN newer work survives.
        Assert.Equal("draft_version_conflict", await Code(await SendAsync(owner, HttpMethod.Delete, path,
            new { requestId = Guid.NewGuid(), expectedVersion = saved.SavedVersion }), HttpStatusCode.Conflict));
        Assert.Equal("draft_request_conflict", await Code(await SendAsync(owner, HttpMethod.Delete, path,
            new { requestId = saved.RequestId, expectedVersion = current.SavedVersion }), HttpStatusCode.Conflict));
        foreach (var invalid in new object[] {
            new { requestId = Guid.Empty, expectedVersion = current.SavedVersion },
            new { requestId = Guid.NewGuid(), expectedVersion = "AQ==" },
            new { requestId = Guid.NewGuid(), expectedVersion = (string?)null },
            new { requestId = Guid.NewGuid() },
            new { requestId = Guid.NewGuid(), expectedVersion = current.SavedVersion, tenantId = Guid.NewGuid() },
        }) Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(owner, HttpMethod.Delete, path, invalid)).StatusCode);
        using (var withoutCsrf = new HttpRequestMessage(HttpMethod.Delete, path)
        { Content = JsonContent.Create(new { requestId = Guid.NewGuid(), expectedVersion = current.SavedVersion }) })
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.SendAsync(withoutCsrf)).StatusCode);
        using var other = application.CreateClient();
        await LoginAsync(other, "other@example.com");
        // AND a foreign or missing identifier is indistinguishable, even when its version is known.
        foreach (var id in new[] { saved.DraftOrderId, Guid.NewGuid() })
            Assert.Equal("draft_not_found", await Code(await SendAsync(other, HttpMethod.Delete, $"{Path}/{id}",
                new { requestId = Guid.NewGuid(), expectedVersion = current.SavedVersion }), HttpStatusCode.NotFound));
        Assert.Equal("Keep newer work", (await owner.GetFromJsonAsync<DraftOrderResponse>(path))!.Draft.Notes);
    }

    private static async Task<SaveDraftOrderResponse> Create(HttpClient client, DraftContent draft)
    {
        var response = await SendAsync(client, HttpMethod.Post, Path, new CreateDraftOrderRequest(Guid.NewGuid(), draft));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
    }

    private static async Task<string?> Code(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();
    }
}
