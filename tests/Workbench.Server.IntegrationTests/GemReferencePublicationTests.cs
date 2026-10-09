// Copyright (c) 2026 The White Stag Collection.

using System.Text.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.Gemology;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class GemReferencePublicationTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private AuthTestApplication _application = null!;
    private Guid _session;
    private GemReferenceDraftService _drafts = null!;
    private GemReferencePublicationService _publisher = null!;
    private int _initialEntryCount;
    private int _initialSourceCount;

    public async Task InitializeAsync()
    {
        _application = await AuthTestApplication.CreateAsync(sqlServer);
        await _application.ProvisionServiceAdminAsync();
        _session = await GemReferenceDraftTests.CreateSessionAsync(_application);
        _drafts = new(_application.WebConnectionString);
        _publisher = new(_application.WebConnectionString);
        _initialEntryCount = await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.Entries");
        _initialSourceCount = await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.SourceAssertions");
    }
    public async Task DisposeAsync() => await _application.DisposeAsync();

    private Task<GemReferenceDraftResponse> DraftAsync(GemReferenceContent content, string? publishedVersion = null) =>
        _drafts.SaveAsync(AuthTestApplication.ServiceAdminId, _session, Guid.NewGuid(), new(content.Id, content, null, publishedVersion), default);
    private static GemReferenceDraftSelection Select(GemReferenceDraftResponse draft) => new(draft.Id, draft.RowVersion);
    private Task<GemReferencePublishOutcome> PublishAsync(GemReferencePublishRequest request) =>
        _publisher.PublishAsync(AuthTestApplication.ServiceAdminId, _session, request, default);
    private Task<T?> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters) =>
        ServiceAdminIdentityDatabaseTests.ScalarAsync<T>(_application.AdminConnectionString, sql, parameters);
    private static GemReferenceContent Other(string name = "Emerald") => GemReferenceSamples.Mineral() with { Id = Guid.NewGuid(), CommonName = name };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ValidBatchPublishesAllEntriesWithOneSuccessAudit(int count)
    {
        // GIVEN selected sourced drafts and an unselected incomplete draft.
        var first = await DraftAsync(GemReferenceSamples.Mineral());
        var second = await DraftAsync(Other());
        var unselected = await DraftAsync(Other("Incomplete") with { Sources = [] });
        // WHEN publishing THEN exactly the selected catalog entries, children, receipt and audit commit.
        var result = await PublishAsync(new(Guid.NewGuid(), count == 1 ? [Select(first)] : [Select(first), Select(second)]));
        Assert.Equal("published", result.Code);
        Assert.Equal(count, result.Entries.Count);
        Assert.All(result.Entries, e => Assert.Equal(8, Convert.FromBase64String(e.RowVersion).Length));
        Assert.Equal(_initialEntryCount + count, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.Entries"));
        Assert.Equal(_initialSourceCount + count * 4, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.SourceAssertions"));
        Assert.Equal(3 - count, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.Drafts"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.PublishRequests"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.PublicationAudit WHERE Outcome='published'"));
        Assert.NotNull(await _drafts.ReadAsync(AuthTestApplication.ServiceAdminId, _session, unselected.Id, default));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("duplicate")]
    [InlineData("draftStale")]
    [InlineData("publishedStale")]
    [InlineData("unsafeSource")]
    [InlineData("sourceCollision")]
    public async Task InvalidDuplicateOrStaleMemberPublishesNothingAndPreservesDrafts(string state)
    {
        // GIVEN a valid member and another member that violates one independent publication boundary.
        var first = await DraftAsync(GemReferenceSamples.Mineral());
        var content = Other();
        if (state == "invalid") content = content with { Species = null };
        if (state == "duplicate") content = content with { CommonName = first.Content.CommonName };
        if (state == "unsafeSource") content = content with { Sources = content.Sources.Select(s => s with { Url = "javascript:alert(1)" }).ToArray() };
        if (state == "sourceCollision") content = content with { Sources = first.Content.Sources };
        string? publishedVersion = null;
        if (state == "publishedStale")
        {
            await GemReferenceTestData.InsertAsync(_application.AdminConnectionString, content);
            publishedVersion = Convert.ToBase64String((await ScalarAsync<byte[]>("SELECT RowVersion FROM Gemology.Entries WHERE Id=@id", ("id", content.Id)))!);
        }
        var second = await DraftAsync(content, publishedVersion);
        if (state == "draftStale") await _drafts.SaveAsync(AuthTestApplication.ServiceAdminId, _session, second.Id,
            new(content.Id, content with { CommonName = "Concurrent draft edit" }, second.RowVersion, publishedVersion), default);
        if (state == "publishedStale") await ScalarAsync<object>("UPDATE Gemology.Entries SET CommonName=N'Concurrent catalog edit' WHERE Id=@id", ("id", content.Id));
        // WHEN publishing THEN no selected change is applied and both drafts survive with durable failure evidence.
        var result = await PublishAsync(new(Guid.NewGuid(), [Select(first), Select(second)]));
        Assert.NotEqual("published", result.Code);
        Assert.Empty(result.Entries);
        Assert.Equal(_initialEntryCount + (state == "publishedStale" ? 1 : 0), await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.Entries"));
        Assert.Equal(2, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.Drafts"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.PublishRequests"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.PublicationAudit WHERE Outcome<>'published'"));
    }

    [Fact]
    public async Task ExactRetryReturnsStoredOutcomeAfterDraftRemovalAndChangedRetryConflicts()
    {
        // GIVEN a valid request whose first response may be lost.
        var first = await DraftAsync(GemReferenceSamples.Mineral());
        var second = await DraftAsync(Other());
        var request = new GemReferencePublishRequest(Guid.NewGuid(), [Select(first), Select(second)]);
        // WHEN replaying through another publisher after successful draft removal THEN the original result is returned once.
        var original = await PublishAsync(request);
        var otherPublisher = new GemReferencePublicationService(_application.WebConnectionString);
        var retry = await otherPublisher.PublishAsync(AuthTestApplication.ServiceAdminId, _session,
            request with { Drafts = request.Drafts.Reverse().ToArray() }, default);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(retry));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.PublishRequests"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.PublicationAudit"));
        // AND changed input conflicts without changing catalog, receipt, or success audit.
        Assert.Equal("request_conflict", (await PublishAsync(request with { Drafts = [Select(first)] })).Code);
        Assert.Equal(_initialEntryCount + 2, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.Entries"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.PublicationAudit WHERE Outcome='published'"));
    }

    [Fact]
    public async Task FailedOutcomeRemainsStableAfterDraftCorrection()
    {
        // GIVEN an invalid request with a durable rejection.
        var draft = await DraftAsync(GemReferenceSamples.Mineral() with { Sources = [] });
        var request = new GemReferencePublishRequest(Guid.NewGuid(), [Select(draft)]);
        var original = await PublishAsync(request);
        // WHEN correcting the draft THEN an exact retry remains the original failure; a new request can publish the correction.
        var corrected = await _drafts.SaveAsync(AuthTestApplication.ServiceAdminId, _session, draft.Id,
            new(draft.EntryId, GemReferenceSamples.Mineral(), draft.RowVersion, null), default);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await PublishAsync(request)));
        Assert.Equal(_initialEntryCount + 0, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.Entries"));
        Assert.Equal("published", (await PublishAsync(new(Guid.NewGuid(), [Select(corrected)]))).Code);
    }

    [Fact]
    public async Task ConcurrentExactRetriesPublishOnce()
    {
        // GIVEN identical requests on independent SQL connections.
        var draft = await DraftAsync(GemReferenceSamples.Mineral());
        var request = new GemReferencePublishRequest(Guid.NewGuid(), [Select(draft)]);
        // WHEN racing THEN both receive identical outcomes and only one publication is recorded.
        var results = await Task.WhenAll(PublishAsync(request), PublishAsync(request));
        Assert.Equal("published", results[0].Code);
        Assert.Equal(JsonSerializer.Serialize(results[0]), JsonSerializer.Serialize(results[1]));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.PublicationAudit"));
    }

    [Fact]
    public async Task ConcurrentDistinctBatchesRejectDuplicateIdentity()
    {
        // GIVEN individually valid drafts of the same normalized shared identity.
        var first = await DraftAsync(GemReferenceSamples.Mineral());
        var second = await DraftAsync(Other(first.Content.CommonName));
        // WHEN racing distinct publish requests THEN only one succeeds and the losing draft survives.
        var results = await Task.WhenAll(PublishAsync(new(Guid.NewGuid(), [Select(first)])), PublishAsync(new(Guid.NewGuid(), [Select(second)])));
        Assert.Single(results, r => r.Code == "published");
        Assert.Single(results, r => r.Code != "published");
        Assert.Equal(_initialEntryCount + 1, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.Entries"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.Drafts"));
    }

    [Fact]
    public async Task RetireAndReplaceChecksFinalIdentity()
    {
        // GIVEN a published identity and its replacement in the same batch.
        var content = GemReferenceSamples.Mineral();
        await GemReferenceTestData.InsertAsync(_application.AdminConnectionString, content);
        var version = Convert.ToBase64String((await ScalarAsync<byte[]>("SELECT RowVersion FROM Gemology.Entries WHERE Id=@id", ("id", content.Id)))!);
        var replacement = Other(content.CommonName);
        var retire = await DraftAsync(content with { IsRetired = true, RedirectEntryId = replacement.Id }, version);
        var create = await DraftAsync(replacement);
        // WHEN replacement is selected before retirement THEN final identities are still valid and stable IDs are retained.
        Assert.Equal("published", (await PublishAsync(new(Guid.NewGuid(), [Select(create), Select(retire)]))).Code);
        Assert.Equal(_initialEntryCount + 2, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.Entries"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.Entries WHERE IsRetired=1 AND RedirectEntryId=@id", ("id", replacement.Id)));
    }

    [Fact]
    public async Task CrossEntryRedirectCycleRejectsWholeBatch()
    {
        // GIVEN two retired drafts redirecting to each other.
        var a = GemReferenceSamples.Mineral(); var b = Other();
        var first = await DraftAsync(a with { IsRetired = true, RedirectEntryId = b.Id });
        var second = await DraftAsync(b with { IsRetired = true, RedirectEntryId = a.Id });
        // WHEN reviewed as a final catalog THEN the cycle prevents all publication.
        Assert.Equal("validation_failed", (await PublishAsync(new(Guid.NewGuid(), [Select(first), Select(second)]))).Code);
        Assert.Equal(_initialEntryCount + 0, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.Entries"));
        Assert.Equal(2, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.Drafts"));
    }

    [Fact]
    public async Task FailedAuditRollsBackCatalogReceiptAndDraftCleanup()
    {
        // GIVEN a valid batch and a SQL audit-write fault.
        var draft = await DraftAsync(GemReferenceSamples.Mineral());
        var request = new GemReferencePublishRequest(Guid.NewGuid(), [Select(draft)]);
        await ScalarAsync<object>("CREATE TRIGGER Gemology.RejectAudit ON Gemology.PublicationAudit AFTER INSERT AS THROW 50045,'Synthetic audit failure.',1;");
        // WHEN publishing THEN the entire success transaction is rolled back.
        await Assert.ThrowsAsync<SqlException>(() => PublishAsync(request));
        Assert.Equal(_initialEntryCount + 0, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.Entries"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.PublishRequests"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.Drafts"));
        // AND after removing the fault the same request can safely complete once.
        await ScalarAsync<object>("DROP TRIGGER Gemology.RejectAudit");
        Assert.Equal("published", (await PublishAsync(request)).Code);
    }

    [Theory]
    [InlineData("sources")]
    [InlineData("changedPayload")]
    [InlineData("staleVersion")]
    [InlineData("injectedAlias")]
    [InlineData("omittedAlias")]
    [InlineData("changedAlias")]
    public async Task DirectSqlPublicationRejectsInvalidSourceOrPayloadAndStaleDraft(string state)
    {
        // GIVEN a saved draft and a caller bypassing the application validation service.
        var content = GemReferenceSamples.Mineral();
        if (state is "omittedAlias" or "changedAlias") content = content with { Aliases = ["Reviewed alias"], Sources = [.. content.Sources, GemReferenceSamples.Source("aliases")] };
        if (state == "sources") content = content with { Sources = [] };
        var draft = await DraftAsync(content);
        var selection = new[] { Select(draft) };
        if (state == "staleVersion") selection = [new(draft.Id, Convert.ToBase64String(new byte[8]))];
        if (state == "changedPayload") content = content with { CommonName = "Unreviewed replacement" };
        await using var connection = new SqlConnection(_application.WebConnectionString);
        await connection.OpenAsync();
        var id = Guid.NewGuid();
        await using var command = GemReferenceCurationSql.Command(connection, null, "PublishDraftBatch", AuthTestApplication.ServiceAdminId, _session);
        command.Parameters.AddWithValue("@RequestId", id);
        command.Parameters.AddWithValue("@SelectionJson", GemReferencePublicationService.CanonicalSelection(selection));
        command.Parameters.AddWithValue("@EntriesJson", JsonSerializer.Serialize(new[] { new { draftId = draft.Id, entryId = content.Id,
            content, identityKey = Convert.ToHexString(GemReferenceInput.IdentityKey(content)), aliases = state is "injectedAlias" or "changedAlias"
                ? new object[] { new { position = 0, name = "Unreviewed alias", normalizedName = "UNREVIEWED ALIAS" } } : [] } }, GemReferenceCurationSql.Json));
        command.Parameters.AddWithValue("@OutcomeJson", JsonSerializer.Serialize(new GemReferencePublishOutcome(id, "published", [], []), GemReferenceCurationSql.Json));
        command.Parameters.AddWithValue("@SummaryJson", "[]");
        // WHEN invoking the restricted command directly THEN SQL independently guards sourced content and selected saved versions.
        Assert.Equal(state is "sources" or "injectedAlias" or "omittedAlias" or "changedAlias" ? 50043 : 50044,
            (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
        Assert.Equal(_initialEntryCount + 0, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.Entries"));
        Assert.Equal(1, await ScalarAsync<int>("SELECT COUNT(*) FROM Gemology.Drafts"));
    }

    [Fact]
    public async Task DurableReceiptRetainsFieldSummaryWithoutHistoricalContent()
    {
        // GIVEN a review containing full content differences.
        var draft = await DraftAsync(GemReferenceSamples.Mineral());
        var review = await _publisher.ReviewAsync(AuthTestApplication.ServiceAdminId, _session, [Select(draft)], default);
        Assert.Contains(review.Entries.SelectMany(e => e.Changes), c => c.After is not null);
        // WHEN publishing THEN the durable response retains changed fields without retaining historical values.
        var outcome = await PublishAsync(new(Guid.NewGuid(), [Select(draft)]));
        Assert.NotEmpty(outcome.Review.SelectMany(e => e.Changes));
        Assert.All(outcome.Review.SelectMany(e => e.Changes), c => { Assert.Null(c.Before); Assert.Null(c.After); });
        Assert.Equal(JsonSerializer.Serialize(outcome), JsonSerializer.Serialize(await _publisher.ReadAsync(
            AuthTestApplication.ServiceAdminId, _session, outcome.RequestId, default)));
    }
}
