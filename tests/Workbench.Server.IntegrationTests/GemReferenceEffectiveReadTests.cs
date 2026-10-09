// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.Gemology;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed partial class GemReferenceTenantEndpointTests
{
    [Fact]
    public async Task MalformedReadBindingRetainsPrivateCachePolicy()
    {
        // GIVEN an authenticated tenant reader whose optional archive flag cannot be parsed.
        // WHEN binding rejects the request THEN the tenant API still prohibits shared/browser caching.
        using var response = await _member.GetAsync(Route + "?includeArchived=not-a-boolean");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }
    private bool _serviceAdminReady;
    private async Task PublishAsync(GemReferenceContent content)
    {
        if (!_serviceAdminReady) { await _app.ProvisionServiceAdminAsync(); _serviceAdminReady = true; }
        var session = await GemReferenceDraftTests.CreateSessionAsync(_app);
        var current = await new GemReferenceAdminReadService(_app.WebConnectionString).DetailAsync(content.Id, default);
        var draft = await new GemReferenceDraftService(_app.WebConnectionString).SaveAsync(Infrastructure.AuthTestApplication.ServiceAdminId, session,
            Guid.NewGuid(), new(content.Id, content, null, current?.RowVersion), default);
        var result = await new GemReferencePublicationService(_app.WebConnectionString).PublishAsync(Infrastructure.AuthTestApplication.ServiceAdminId, session,
            new(Guid.NewGuid(), [new(draft.Id, draft.RowVersion)]), default);
        Assert.Equal("published", result.Code);
    }
    private async Task<GemReferencePageResponse> PageAsync(string search, HttpClient? client = null) =>
        (await (client ?? _member).GetFromJsonAsync<GemReferencePageResponse>(Route + "?" + search))!;

    [Fact]
    public async Task EffectiveFieldsDriveSearchAndInheritedPublicationWhileOtherTenantRemainsShared()
    {
        // GIVEN a shared reference customized only for one tenant.
        var content = GemReferenceSamples.Mineral() with { CommonName = "Search shared", Group = "Shared family", Aliases = ["Shared alias"], Sources = [.. GemReferenceSamples.Mineral().Sources, GemReferenceSamples.Source("group"), GemReferenceSamples.Source("aliases")] };
        await Infrastructure.GemReferenceTestData.InsertAsync(_app.AdminConnectionString, content);
        var detail = (await _member.GetFromJsonAsync<GemReferenceDetailResponse>(Route + "/" + content.Id))!;
        var customized = await SavedAsyncSend(HttpMethod.Put, "/" + content.Id + "/overrides", new GemReferenceOverridesRequest(
            new Dictionary<string, GemReferenceFieldOverride> { ["commonName"] = Replace("Tenant search"), ["group"] = Replace("Tenant family"), ["aliases"] = Replace(new[] { "Tenant %_[] alias" }) }, detail.EffectiveVersion!));
        // WHEN browsing by each effective field THEN displaced names/aliases/filters no longer match in this tenant.
        foreach (var query in new[] { "Tenant search", "Tenant %_[] alias" }) Assert.Equal(content.Id, Assert.Single((await PageAsync("query=" + Uri.EscapeDataString(query))).Entries).Id);
        foreach (var query in new[] { "Search shared", "Shared alias" }) Assert.Empty((await PageAsync("query=" + Uri.EscapeDataString(query))).Entries);
        Assert.Equal(content.Id, Assert.Single((await PageAsync("group=TENANT%20FAMILY&materialKind=mineral")).Entries).Id);
        Assert.Empty((await PageAsync("group=Shared%20family")).Entries);
        Assert.Equal(content.Id, Assert.Single((await PageAsync("query=Shared%20alias", _other)).Entries).Id);
        Assert.Equal("workbenchReferenceCustomized", customized.Layer);
        // WHEN shared taxonomy and its sources are published THEN inherited search and reset receive the current correction.
        var correction = content with { Species = "Publication species", Sources = content.Sources.Select(source => source with { Title = "Publication correction" }).ToArray() };
        await PublishAsync(correction);
        Assert.Equal(content.Id, Assert.Single((await PageAsync("query=publication%20species")).Entries).Id);
        var current = (await _member.GetFromJsonAsync<GemReferenceDetailResponse>(Route + "/" + content.Id))!;
        Assert.Equal("Tenant search", current.CommonName);
        var reset = await SavedAsyncSend(HttpMethod.Post, "/" + content.Id + "/reset", new GemReferenceResetRequest(null, current.EffectiveVersion!));
        Assert.Equal("Search shared", reset.CommonName); Assert.Empty(reset.Overrides);
        Assert.All(reset.SourceAssertions, source => Assert.Equal("Publication correction", source.Title));
        Assert.Empty((await PageAsync("query=Tenant%20search")).Entries);
    }

    [Fact]
    public async Task InvalidAndCollidingEffectiveRowsStaySearchableWithReviewReasons()
    {
        // GIVEN a valid clear on a non-mineral shared reference and a private addition.
        var shared = GemReferenceSamples.Mineral() with { MaterialKind = "organic", CommonName = "Review pearl", Species = null, Variety = null, Sources = [GemReferenceSamples.Source("commonName"), GemReferenceSamples.Source("materialKind")] };
        await Infrastructure.GemReferenceTestData.InsertAsync(_app.AdminConnectionString, shared);
        var detail = (await _member.GetFromJsonAsync<GemReferenceDetailResponse>(Route + "/" + shared.Id))!;
        await SavedAsyncSend(HttpMethod.Put, "/" + shared.Id + "/overrides", new GemReferenceOverridesRequest(new Dictionary<string, GemReferenceFieldOverride> { ["species"] = new("clear", null, []) }, detail.EffectiveVersion!));
        // WHEN publication makes the clear invalid THEN the raw entry stays searchable with field reasons.
        await PublishAsync(shared with { MaterialKind = "mineral", Species = "Review species", Sources = [.. shared.Sources, GemReferenceSamples.Source("species")] });
        var invalid = Assert.Single((await PageAsync("query=Review%20pearl")).Entries);
        Assert.True(invalid.NeedsReview); Assert.Contains("species", invalid.ReviewReasons.Keys);
        Assert.False(Assert.Single((await PageAsync("query=Review%20pearl", _other)).Entries).NeedsReview);
        var current = (await _member.GetFromJsonAsync<GemReferenceDetailResponse>(Route + "/" + shared.Id))!;
        var rejected = await FailureAsync(HttpMethod.Put, "/" + shared.Id + "/overrides", new GemReferenceOverridesRequest(new Dictionary<string, GemReferenceFieldOverride>(current.Overrides) { ["description"] = Replace("Unrelated") }, current.EffectiveVersion!), HttpStatusCode.Conflict, "needs_review");
        Assert.True(rejected.GetProperty("errors").TryGetProperty("species", out _));
        await SavedAsyncSend(HttpMethod.Post, "/" + shared.Id + "/reset", new GemReferenceResetRequest("species", current.EffectiveVersion!));
        var addition = Addition("Collision pearl"); await CreateAsync(addition);
        // AND a later shared exact identity collision preserves both origins and flags both stable IDs.
        var collision = addition with { Id = Guid.NewGuid(), Sources = [GemReferenceSamples.Source("commonName"), GemReferenceSamples.Source("materialKind")] };
        await PublishAsync(collision);
        var rows = (await PageAsync("query=Collision%20pearl")).Entries;
        Assert.Equal(2, rows.Count); Assert.All(rows, row => { Assert.True(row.NeedsReview); Assert.Contains("identity", row.ReviewReasons.Keys); });
        Assert.Equal(collision.Id, Assert.Single((await PageAsync("query=Collision%20pearl", _other)).Entries).Id);
    }

    [Fact]
    public async Task MixedOriginPagesPreserveSqlOrderingAndBindArchiveContextAndOrigin()
    {
        // GIVEN 52 SQL-case-equal names, including tenant/shared entries sharing a GUID at the page boundary.
        for (var number = 1; number <= 51; number++)
        {
            var id = Guid.Parse($"{52 - number:x8}-0000-0000-0000-{number:000000000000}");
            var content = GemReferenceSamples.Mineral() with { Id = id, CommonName = number % 2 == 0 ? "Paged gem" : "paged gem", Group = "Paged family", Species = "Species " + number };
            if (number % 2 == 0) await CreateAsync(content with { Sources = [] });
            else await Infrastructure.GemReferenceTestData.InsertAsync(_app.AdminConnectionString, content);
        }
        var overlapId = Guid.Parse("00000002-0000-0000-0000-000000000050");
        await Infrastructure.GemReferenceTestData.InsertAsync(_app.AdminConnectionString, GemReferenceSamples.Mineral() with { Id = overlapId, CommonName = "Paged gem", Group = "Paged family", Species = "Shared counterpart" });
        // WHEN traversing effective pages THEN SQL name/GUID ordering plus origin retains every scoped identity exactly once.
        var first = await PageAsync("group=Paged%20family"); Assert.Equal(50, first.Entries.Count); Assert.NotNull(first.NextCursor);
        var second = await PageAsync("group=Paged%20family&cursor=" + Uri.EscapeDataString(first.NextCursor!)); Assert.Equal(2, second.Entries.Count); Assert.Null(second.NextCursor);
        var actual = first.Entries.Concat(second.Entries).Select(row => (row.Id, row.Origin)).ToArray();
        Assert.Equal(52, actual.Distinct().Count());
        await using var connection = new SqlConnection(_app.AdminConnectionString); await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT Id,Origin FROM OPENJSON(@rows) WITH (Id uniqueidentifier '$.id', Origin nvarchar(16) '$.origin', CommonName nvarchar(200) '$.commonName')
            ORDER BY CommonName COLLATE Latin1_General_100_CI_AS,Id,Origin COLLATE Latin1_General_100_BIN2;
            """, connection);
        command.Parameters.Add("@rows", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(first.Entries.Concat(second.Entries), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var expected = new List<(Guid, string)>(); await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) expected.Add((reader.GetGuid(0), reader.GetString(1)));
        Assert.Equal(expected, actual); Assert.Equal((overlapId, "tenant"), actual[49]); Assert.Equal((overlapId, "workbench"), actual[50]);
        // AND continuations reject changed query, filters, or includeArchived context.
        foreach (var search in new[] { "query=Paged", "materialKind=mineral", "group=Paged%20family&includeArchived=true" })
            Assert.Equal(HttpStatusCode.BadRequest, (await _member.GetAsync(Route + "?" + search + "&cursor=" + Uri.EscapeDataString(first.NextCursor!))).StatusCode);
        // AND a pre-change v1 shared cursor remains valid for ordinary browse, but cannot opt into archived rows.
        var old = GemReferenceCursor.Encode(new(first.Entries[48].CommonName, first.Entries[48].Id), new(null, null, "PAGED FAMILY"));
        Assert.Equal(3, (await PageAsync("group=Paged%20family&cursor=" + Uri.EscapeDataString(old))).Entries.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await _member.GetAsync(Route + "?group=Paged%20family&includeArchived=true&cursor=" + Uri.EscapeDataString(old))).StatusCode);
        // AND explicit origin distinguishes overlapping details while the default selects tenant even when archived.
        Assert.Equal("tenant", (await _member.GetFromJsonAsync<GemReferenceDetailResponse>(Route + "/" + overlapId))!.Origin);
        Assert.Equal("workbench", (await _member.GetFromJsonAsync<GemReferenceDetailResponse>(Route + "/" + overlapId + "?origin=workbench"))!.Origin);
        Assert.Equal("tenant", (await _member.GetFromJsonAsync<GemReferenceDetailResponse>(Route + "/" + overlapId + "?origin=tenant"))!.Origin);
        Assert.Equal(HttpStatusCode.BadRequest, (await _member.GetAsync(Route + "/" + overlapId + "?origin=unknown")).StatusCode);
        Assert.Equal(27, (await PageAsync("group=Paged%20family", _other)).Entries.Count);
    }
}
