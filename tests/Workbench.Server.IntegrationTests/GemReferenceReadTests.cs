// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class GemReferenceReadTests(SqlServerFixture sqlServer)
{
    private const string Route = "/api/beta/gem-reference";

    [Fact]
    public async Task SharedReadsRequireTenantAuthenticationAndExposeOnlySharedContent()
    {
        // GIVEN a shared entry and separate tenant sessions.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        var content = GemReferenceSamples.Mineral();
        await GemReferenceTestData.InsertAsync(app.AdminConnectionString, content);
        using var anonymous = app.CreateClient();
        // WHEN unauthenticated THEN both catalog routes deny access.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Route)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Route}/{content.Id}")).StatusCode);
        using var member = app.CreateClient();
        using var other = app.CreateClient();
        await LoginAsync(member, "member@example.com");
        await LoginAsync(other, "other@example.com");
        // WHEN reading from both tenants THEN the same shared identities appear with no tenant fields.
        foreach (var client in new[] { member, other })
        {
            using var response = await client.GetAsync(Route);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.Private);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var row = Assert.Single(body.GetProperty("entries").EnumerateArray());
            Assert.Equal(content.Id, row.GetProperty("id").GetGuid());
            Assert.Equal("workbenchReference", row.GetProperty("layer").GetString());
            Assert.False(row.TryGetProperty("tenantId", out _));
        }
    }

    [Fact]
    public async Task SearchFindsEveryNameAndTaxonomyFieldAsLiteralText()
    {
        // GIVEN separately searchable taxonomy and a literal punctuation alias.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        await LoginAsync(client, "member@example.com");
        var content = GemReferenceSamples.Mineral() with { Group = "Test family", Aliases = ["Red gem", "100%_[]\\"],
            Sources = [.. GemReferenceSamples.Mineral().Sources, GemReferenceSamples.Source("group"), GemReferenceSamples.Source("aliases")] };
        await GemReferenceTestData.InsertAsync(app.AdminConnectionString, content);
        // WHEN matching each supported field THEN matching is case-insensitive and literal.
        foreach (var query in new[] { "RUBY", "red gem", "TEST family", "corundum", "%_[]\\" })
            Assert.Equal(content.Id, Assert.Single((await PageAsync(client, "?query=" + Uri.EscapeDataString(query)))
                .GetProperty("entries").EnumerateArray()).GetProperty("id").GetGuid());
        foreach (var query in new[] { "absent", "' OR 1=1--" })
            Assert.Empty((await PageAsync(client, "?query=" + Uri.EscapeDataString(query))).GetProperty("entries").EnumerateArray());
        Assert.Single((await PageAsync(client, "?materialKind=mineral&group=test%20family")).GetProperty("entries").EnumerateArray());
        Assert.Empty((await PageAsync(client, "?materialKind=organic")).GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task DetailRetainsRetirementSourcesAndMissingTaxonomy()
    {
        // GIVEN an organic reference without mineral taxonomy and a retired identity.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        await LoginAsync(client, "member@example.com");
        Assert.Empty((await PageAsync(client)).GetProperty("entries").EnumerateArray());
        var source = GemReferenceSamples.Source("notableLocality");
        var pearl = GemReferenceSamples.Mineral() with { MaterialKind = "organic", CommonName = "Pearl",
            Species = null, Variety = null, Sources = [GemReferenceSamples.Source("materialKind"), GemReferenceSamples.Source("commonName"), source],
            NotableLocality = new("Test coast", "Synthetic locality claim", source.ReviewedOn, source.Id) };
        await GemReferenceTestData.InsertAsync(app.AdminConnectionString, pearl);
        var retired = GemReferenceSamples.Mineral() with { Id = Guid.NewGuid(), IsRetired = true,
            RetirementExplanation = "Reclassified", RedirectEntryId = pearl.Id };
        await GemReferenceTestData.InsertAsync(app.AdminConnectionString, retired);
        // WHEN reading THEN absent taxonomy stays null and each assertion carries its own provenance.
        var detail = await client.GetFromJsonAsync<JsonElement>($"{Route}/{pearl.Id}");
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("species").ValueKind);
        Assert.Equal("Synthetic locality claim", detail.GetProperty("notableLocality").GetProperty("scope").GetString());
        Assert.All(detail.GetProperty("sourceAssertions").EnumerateArray(), row => {
            Assert.Equal("workbench", row.GetProperty("attribution").GetString());
            Assert.Equal("2026-09-29", row.GetProperty("reviewedOn").GetString());
        });
        Assert.Single((await PageAsync(client)).GetProperty("entries").EnumerateArray());
        var retained = await client.GetFromJsonAsync<JsonElement>($"{Route}/{retired.Id}");
        Assert.True(retained.GetProperty("retirement").GetProperty("isRetired").GetBoolean());
        Assert.Equal(pearl.Id, retained.GetProperty("retirement").GetProperty("redirectEntryId").GetGuid());
        Assert.Equal("Reclassified", retained.GetProperty("retirement").GetProperty("explanation").GetString());
        Assert.Equal(8, Convert.FromBase64String(retained.GetProperty("rowVersion").GetString()!).Length);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Route}/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task PagesWithCaseEqualNamesKeepEveryIdAndBindFilters()
    {
        // GIVEN more than a page of case-equal display names with distinct identities.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        await LoginAsync(client, "member@example.com");
        var expected = new HashSet<Guid>();
        for (var n = 0; n < 52; n++)
        {
            var entry = GemReferenceSamples.Mineral() with { Id = Guid.NewGuid(), CommonName = n % 2 == 0 ? "Ruby" : "ruby", Species = $"Species {n}" };
            expected.Add(entry.Id);
            await GemReferenceTestData.InsertAsync(app.AdminConnectionString, entry);
        }
        // WHEN traversing THEN ordering ties do not lose or repeat identities.
        var first = await PageAsync(client);
        Assert.Equal(50, first.GetProperty("entries").GetArrayLength());
        var cursor = first.GetProperty("nextCursor").GetString()!;
        var second = await PageAsync(client, "?cursor=" + Uri.EscapeDataString(cursor));
        Assert.Equal(2, second.GetProperty("entries").GetArrayLength());
        var ids = first.GetProperty("entries").EnumerateArray().Concat(second.GetProperty("entries").EnumerateArray()).Select(row => row.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(52, ids.Distinct().Count());
        Assert.True(expected.SetEquals(ids));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
        // AND changed filters cannot reuse the original continuation.
        foreach (var change in new[] { "query=Ruby", "materialKind=mineral", "group=Test" })
            await AssertProblemAsync(client, $"?{change}&cursor={Uri.EscapeDataString(cursor)}", "invalid_cursor");
    }

    [Theory]
    [InlineData("?materialKind=unknown", "invalid_filter")]
    [InlineData("?query=Ru%0Aby", "invalid_query")]
    [InlineData("?group=Ru%0Aby", "invalid_filter")]
    [InlineData("?cursor=not-base64", "invalid_cursor")]
    [InlineData("?cursor=bnVsbA==", "invalid_cursor")]
    [InlineData("?cursor=e30=", "invalid_cursor")]
    public async Task MalformedQueriesFailPredictably(string query, string code)
    {
        // GIVEN an authenticated reader WHEN a malformed query is sent THEN a bounded problem is returned.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        await LoginAsync(client, "member@example.com");
        await AssertProblemAsync(client, query, code);
    }

    [Fact]
    public async Task ExcessiveQueriesAndFailedReadsRemainDistinctFromEmptyResults()
    {
        // GIVEN a reader and an empty catalog.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        await LoginAsync(client, "member@example.com");
        // WHEN inputs exceed bounds THEN they are rejected before database browsing.
        await AssertProblemAsync(client, "?query=" + new string('a', 201), "invalid_query");
        await AssertProblemAsync(client, "?group=" + new string('a', 201), "invalid_filter");
        await AssertProblemAsync(client, "?cursor=" + new string('a', 4097), "invalid_cursor");
        // AND denied SQL reads fail rather than falsely reporting no matches, then recover on retry.
        await using var connection = new SqlConnection(app.AdminConnectionString);
        await connection.OpenAsync();
        await new SqlCommand("DENY SELECT ON Gemology.Entries TO workbench_web", connection).ExecuteNonQueryAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, (await client.GetAsync(Route)).StatusCode);
        await new SqlCommand("GRANT SELECT ON Gemology.Entries TO workbench_web", connection).ExecuteNonQueryAsync();
        Assert.Empty((await PageAsync(client)).GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task DetailDoesNotCombineClassificationAndSourcesAcrossAnUpdate()
    {
        // GIVEN a reference whose name and supporting sources are changed atomically.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = app.CreateClient();
        await LoginAsync(client, "member@example.com");
        var original = GemReferenceSamples.Mineral() with { CommonName = "Before" };
        original = original with { Sources = original.Sources.Select(source => source with { Title = "Before" }).ToArray() };
        await GemReferenceTestData.InsertAsync(app.AdminConnectionString, original);
        var writer = Task.Run(async () => {
            await using var connection = new SqlConnection(app.AdminConnectionString);
            await connection.OpenAsync();
            for (var n = 0; n < 10; n++)
            {
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
                await using var update = new SqlCommand("UPDATE Gemology.Entries SET CommonName=@name WHERE Id=@id; UPDATE Gemology.SourceAssertions SET Title=@name WHERE EntryId=@id", connection, transaction);
                update.Parameters.AddWithValue("@id", original.Id);
                update.Parameters.AddWithValue("@name", n % 2 == 0 ? "After" : "Before");
                await update.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
        });
        // WHEN reads overlap publication THEN each response contains one coherent state.
        for (var n = 0; n < 10; n++)
        {
            var detail = await client.GetFromJsonAsync<JsonElement>($"{Route}/{original.Id}");
            var name = detail.GetProperty("commonName").GetString();
            Assert.All(detail.GetProperty("sourceAssertions").EnumerateArray(), source => Assert.Equal(name, source.GetProperty("title").GetString()));
        }
        await writer;
    }

    private static Task<JsonElement> PageAsync(HttpClient client, string query = "") => client.GetFromJsonAsync<JsonElement>(Route + query);

    private static async Task AssertProblemAsync(HttpClient client, string query, string code)
    {
        using var response = await client.GetAsync(Route + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
    }

    private static async Task LoginAsync(HttpClient client, string email)
    {
        var token = await client.GetFromJsonAsync<JsonElement>("/api/beta/auth/antiforgery");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/beta/auth/login")
        { Content = JsonContent.Create(new { email, password = AuthTestApplication.AdminPassword }) };
        request.Headers.Add("X-CSRF-TOKEN", token.GetProperty("requestToken").GetString());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
