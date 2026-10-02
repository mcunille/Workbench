// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Workbench.Server.Gemology;
using Workbench.Server.Identity;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class GemReferenceCurationEndpointTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private const string Prefix = "/api/beta/service-admin/gem-reference";
    private const string AdminAuth = "/api/beta/service-admin/auth";
    private AuthTestApplication _application = null!;
    private HttpClient _admin = null!;
    public async Task InitializeAsync()
    {
        _application = await AuthTestApplication.CreateAsync(sqlServer);
        await _application.ProvisionServiceAdminAsync();
        _admin = _application.CreateClient();
    }
    public async Task DisposeAsync()
    {
        _admin.Dispose();
        await _application.DisposeAsync();
    }

    private static async Task<string> CsrfAsync(HttpClient client, string prefix)
    {
        var bootstrap = await client.GetAsync(prefix + "/antiforgery");
        Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
        return (await bootstrap.Content.ReadFromJsonAsync<AntiforgeryResponse>())!.RequestToken;
    }
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object body, string auth = AdminAuth)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", await CsrfAsync(client, auth));
        return await client.SendAsync(request);
    }
    private async Task LoginAsync() => Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(_admin, HttpMethod.Post,
        AdminAuth + "/login", new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.ServiceAdminPassword })).StatusCode);

    [Fact]
    public async Task AdminStagesReviewsPublishesAndTenantReadsOnlyPublishedContent()
    {
        // GIVEN independently authenticated admin and tenant browser sessions.
        await LoginAsync();
        using var tenant = _application.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(tenant, HttpMethod.Post, "/api/beta/auth/login",
            new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.AdminPassword }, "/api/beta/auth")).StatusCode);
        var content = GemReferenceSamples.Mineral();
        var id = Guid.NewGuid();
        // WHEN staging THEN the admin can reload it, while the tenant still sees an empty shared catalog.
        var save = await SendAsync(_admin, HttpMethod.Put, Prefix + "/drafts/" + id, new GemReferenceDraftSaveRequest(content.Id, content, null, null));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        Assert.True(save.Headers.CacheControl!.Private);
        Assert.True(save.Headers.CacheControl.NoStore);
        var draft = (await save.Content.ReadFromJsonAsync<GemReferenceDraftResponse>())!;
        Assert.Equal(draft.RowVersion, (await _admin.GetFromJsonAsync<GemReferenceDraftResponse>(Prefix + "/drafts/" + id))!.RowVersion);
        Assert.Single((await _admin.GetFromJsonAsync<GemReferenceDraftPage>(Prefix + "/drafts"))!.Drafts);
        Assert.Empty((await tenant.GetFromJsonAsync<GemReferencePageResponse>("/api/beta/gem-reference"))!.Entries);
        var selection = new[] { new GemReferenceDraftSelection(id, draft.RowVersion) };
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(_admin, HttpMethod.Post, Prefix + "/review", new GemReferenceReviewRequest(selection))).StatusCode);
        // WHEN publishing THEN both authorities read the sourced shared entry and the durable outcome/audit is attributed to the admin.
        var request = new GemReferencePublishRequest(Guid.NewGuid(), selection);
        var publish = await SendAsync(_admin, HttpMethod.Post, Prefix + "/publish", request);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        var outcome = (await publish.Content.ReadFromJsonAsync<GemReferencePublishOutcome>())!;
        Assert.Equal("published", outcome.Code);
        Assert.Equal(request.RequestId, (await _admin.GetFromJsonAsync<GemReferencePublishOutcome>(Prefix + "/publications/" + request.RequestId))!.RequestId);
        Assert.Single((await tenant.GetFromJsonAsync<GemReferencePageResponse>("/api/beta/gem-reference"))!.Entries);
        Assert.Single((await _admin.GetFromJsonAsync<GemReferencePageResponse>(Prefix + "?query=Corundum"))!.Entries);
        Assert.Equal(content.Id, (await _admin.GetFromJsonAsync<GemReferenceDetailResponse>(Prefix + "/" + content.Id))!.Id);
        var audit = Assert.Single((await _admin.GetFromJsonAsync<GemReferencePublicationAuditPage>(Prefix + "/publication-audit"))!.Events);
        Assert.Equal(AuthTestApplication.ServiceAdminId, audit.AccountId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _admin.GetAsync("/api/beta/items")).StatusCode);
        // AND a response-lost exact HTTP retry returns the same published rowversion.
        var replay = await SendAsync(_admin, HttpMethod.Post, Prefix + "/publish", request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(outcome.Entries.Single().RowVersion, (await replay.Content.ReadFromJsonAsync<GemReferencePublishOutcome>())!.Entries.Single().RowVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TenantAndAnonymousSessionsCannotUseAnyCurationRoute(bool tenantSession)
    {
        // GIVEN an anonymous or tenant-only session.
        if (tenantSession) Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(_admin, HttpMethod.Post, "/api/beta/auth/login",
            new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.AdminPassword }, "/api/beta/auth")).StatusCode);
        var id = Guid.NewGuid();
        // WHEN calling each route directly THEN none obtains curation authority.
        foreach (var path in new[] { "", "/" + id, "/drafts", "/drafts/" + id, "/publications/" + id, "/publication-audit" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await _admin.GetAsync(Prefix + path)).StatusCode);
        foreach (var path in new[] { "/review", "/publish" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(_admin, HttpMethod.Post, Prefix + path,
                new { requestId = id, drafts = new[] { new { draftId = id, expectedDraftRowVersion = Convert.ToBase64String(new byte[8]) } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(_admin, HttpMethod.Put, Prefix + "/drafts/" + id,
            new GemReferenceDraftSaveRequest(id, GemReferenceSamples.Mineral() with { Id = id }, null, null))).StatusCode);
    }

    [Fact]
    public async Task WritesRequireAdminAntiforgeryAndRevocationEndsReadAuthority()
    {
        // GIVEN a signed-in admin without a CSRF request token.
        await LoginAsync();
        // WHEN attempting writes THEN antiforgery protects all write routes.
        foreach (var path in new[] { "/review", "/publish" })
            Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsJsonAsync(Prefix + path, new { drafts = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PutAsJsonAsync(Prefix + "/drafts/" + Guid.NewGuid(), new { })).StatusCode);
        // AND revoking the session ends authority on the next request.
        await _application.MaintainServiceAdminAsync("RevokeServiceAdminSessions");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _admin.GetAsync(Prefix + "/drafts")).StatusCode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("oversized")]
    [InlineData("badVersion")]
    public async Task MalformedSelectionReturnsProblemDetails(string state)
    {
        // GIVEN an authorized caller with a malformed selected batch.
        await LoginAsync();
        var member = new GemReferenceDraftSelection(Guid.NewGuid(), state == "badVersion" ? "bad" : Convert.ToBase64String(new byte[8]));
        object? drafts = state switch { "null" => null, "empty" => Array.Empty<object>(), "duplicate" => new[] { member, member },
            "oversized" => Enumerable.Range(0, 51).Select(_ => member with { DraftId = Guid.NewGuid() }).ToArray(), _ => new[] { member } };
        // WHEN binding review/publish THEN malformed input is a 400 Problem Details, with no receipt.
        foreach (var path in new[] { "/review", "/publish" })
        {
            var response = await SendAsync(_admin, HttpMethod.Post, Prefix + path, new { requestId = Guid.NewGuid(), drafts });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        }
        Assert.Equal(0, await ServiceAdminIdentityDatabaseTests.ScalarAsync<int>(_application.AdminConnectionString, "SELECT COUNT(*) FROM Gemology.PublishRequests"));
    }

    [Fact]
    public async Task DraftBindingRejectsNullCollectionsAndOversizedBody()
    {
        // GIVEN an authorized caller and structurally unreadable content.
        await LoginAsync();
        var content = GemReferenceSamples.Mineral();
        // WHEN saving malformed content THEN the server returns predictable errors without saving it.
        foreach (var candidate in new object?[] { null, content with { Aliases = null! }, content with { Sources = null! }, content with { Sources = [null!] } })
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(_admin, HttpMethod.Put, Prefix + "/drafts/" + Guid.NewGuid(),
                new { entryId = content.Id, content = candidate })).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await SendAsync(_admin, HttpMethod.Put, Prefix + "/drafts/" + Guid.NewGuid(),
            new { entryId = content.Id, content = content with { Description = new string('x', 1024 * 1024) } })).StatusCode);
    }

    [Fact]
    public async Task InvalidBatchReturns422PreservesDraftAndReceiptIsPrivateToActor()
    {
        // GIVEN an incomplete saved draft.
        await LoginAsync();
        var content = GemReferenceSamples.Mineral() with { Sources = [] };
        var id = Guid.NewGuid();
        var draft = (await (await SendAsync(_admin, HttpMethod.Put, Prefix + "/drafts/" + id,
            new GemReferenceDraftSaveRequest(content.Id, content, null, null))).Content.ReadFromJsonAsync<GemReferenceDraftResponse>())!;
        // WHEN publishing THEN failure is durable and the draft remains editable.
        var requestId = Guid.NewGuid();
        var response = await SendAsync(_admin, HttpMethod.Post, Prefix + "/publish", new GemReferencePublishRequest(requestId, [new(id, draft.RowVersion)]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync(Prefix + "/drafts/" + id)).StatusCode);
        // AND another service admin cannot read that request's private outcome.
        await ServiceAdminIdentityDatabaseTests.ScalarAsync<object>(_application.AdminConnectionString, """
            INSERT ServiceAdministration.Accounts(Id,Email,NormalizedEmail,PasswordHash,IsEnabled,SecurityVersion,CreatedAtUtc)
            SELECT NEWID(),N'other-admin@example.com',N'OTHER-ADMIN@EXAMPLE.COM',PasswordHash,1,1,SYSUTCDATETIME() FROM ServiceAdministration.Accounts WHERE Id=@id
            """, ("id", AuthTestApplication.ServiceAdminId));
        using var other = _application.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(other, HttpMethod.Post, AdminAuth + "/login",
            new { email = "other-admin@example.com", password = AuthTestApplication.ServiceAdminPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(Prefix + "/publications/" + requestId)).StatusCode);
    }
}
