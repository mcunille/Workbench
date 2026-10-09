// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Workbench.Server.Gemology;
using Workbench.Server.Identity;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed partial class GemReferenceTenantEndpointTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private const string Route = "/api/beta/gem-reference";
    private AuthTestApplication _app = null!;
    private HttpClient _member = null!;
    private HttpClient _other = null!;
    public async Task InitializeAsync()
    {
        _app = await AuthTestApplication.CreateAsync(sqlServer);
        _member = _app.CreateClient(); _other = _app.CreateClient();
        await LoginAsync(_member, "member@example.com"); await LoginAsync(_other, "other@example.com");
    }
    public async Task DisposeAsync() { _member.Dispose(); _other.Dispose(); await _app.DisposeAsync(); }
    private static GemReferenceContent Addition(string name = "Private pearl") => new(Guid.NewGuid(), "organic", name,
        null, null, null, null, [], [], null, false, null, null);
    private static GemReferenceFieldOverride Replace<T>(T value) => new("replace", JsonSerializer.SerializeToElement(value), []);
    private static async Task<string> TokenAsync(HttpClient client, string auth = "/api/beta/auth") =>
        (await client.GetFromJsonAsync<AntiforgeryResponse>(auth + "/antiforgery"))!.RequestToken;
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object body)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", await TokenAsync(client)); return await client.SendAsync(request);
    }
    private static async Task LoginAsync(HttpClient client, string email) => Assert.Equal(HttpStatusCode.NoContent,
        (await SendAsync(client, HttpMethod.Post, "/api/beta/auth/login", new { email, password = AuthTestApplication.AdminPassword })).StatusCode);
    private static async Task<GemReferenceDetailResponse> SavedAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.Private); Assert.True(response.Headers.CacheControl?.NoStore);
            return (await response.Content.ReadFromJsonAsync<GemReferenceDetailResponse>())!;
        }
    }
    private Task<GemReferenceDetailResponse> CreateAsync(GemReferenceContent content) => SavedAsyncSend(HttpMethod.Post, "/tenant-entries", content);
    private async Task<GemReferenceDetailResponse> SavedAsyncSend(HttpMethod method, string path, object body) =>
        await SavedAsync(await SendAsync(_member, method, Route + path, body));
    private async Task<JsonElement> FailureAsync(HttpMethod method, string path, object body, HttpStatusCode status, string code, HttpClient? client = null)
    {
        using var response = await SendAsync(client ?? _member, method, Route + path, body);
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.Private); Assert.True(response.Headers.CacheControl?.NoStore);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(code, problem.GetProperty("code").GetString()); return problem;
    }

    [Fact]
    public async Task MemberLifecycleReturnsCurrentDetailAndInaccessibleIdsRevealNothing()
    {
        // GIVEN an ordinary tenant member's addition and an independent tenant session.
        var content = Addition(); var created = await CreateAsync(content);
        var otherContent = Addition("Other private pearl");
        var otherCreated = await SavedAsync(await SendAsync(_other, HttpMethod.Post, Route + "/tenant-entries", otherContent));
        Assert.Equal(otherCreated.Id, Assert.Single((await PageAsync("query=private", _other)).Entries).Id);
        Assert.Equal(created.Id, Assert.Single((await PageAsync("query=private")).Entries).Id);
        Assert.Equal(HttpStatusCode.NotFound, (await _member.GetAsync(Route + "/" + otherContent.Id)).StatusCode);
        Assert.Equal("tenant", created.Origin); Assert.Equal("tenantEntry", created.Layer);
        // WHEN updating with a stale version THEN the HTTP conflict includes only this tenant's accessible detail.
        var updated = await SavedAsyncSend(HttpMethod.Put, "/tenant-entries/" + content.Id, new GemReferenceTenantUpdateRequest(content with { CommonName = "Changed pearl" }, created.EffectiveVersion!));
        var stale = await FailureAsync(HttpMethod.Put, "/tenant-entries/" + content.Id, new GemReferenceTenantUpdateRequest(content, created.EffectiveVersion!), HttpStatusCode.Conflict, "stale_entry");
        Assert.Equal("Changed pearl", stale.GetProperty("current").GetProperty("commonName").GetString());
        // AND cross-tenant substitutions give normal not-found without current data.
        var absent = await FailureAsync(HttpMethod.Put, "/tenant-entries/" + content.Id, new GemReferenceTenantUpdateRequest(content, updated.EffectiveVersion!), HttpStatusCode.NotFound, "not_found", _other);
        Assert.False(absent.TryGetProperty("current", out var current) && current.ValueKind != JsonValueKind.Null);
        Assert.Equal(HttpStatusCode.NotFound, (await _other.GetAsync(Route + "/" + content.Id)).StatusCode);
        // WHEN archiving THEN ordinary search excludes it, explicit browse and detail retain it, and restore advances its version.
        var archived = await SavedAsyncSend(HttpMethod.Post, "/tenant-entries/" + content.Id + "/archive", new GemReferenceTenantVersionRequest(updated.EffectiveVersion!));
        Assert.True(archived.IsArchived);
        Assert.Empty((await _member.GetFromJsonAsync<GemReferencePageResponse>(Route + "?query=Changed%20pearl"))!.Entries);
        Assert.Equal(content.Id, Assert.Single((await _member.GetFromJsonAsync<GemReferencePageResponse>(Route + "?query=Changed%20pearl&includeArchived=true"))!.Entries).Id);
        Assert.True((await _member.GetFromJsonAsync<GemReferenceDetailResponse>(Route + "/" + content.Id))!.IsArchived);
        Assert.False((await SavedAsyncSend(HttpMethod.Post, "/tenant-entries/" + content.Id + "/restore", new GemReferenceTenantVersionRequest(archived.EffectiveVersion!))).IsArchived);
    }

    [Fact]
    public async Task EveryTenantRouteRequiresMemberSessionAndWritesRequireAntiforgery()
    {
        // GIVEN anonymous and service-admin-only sessions, neither has tenant member authority.
        using var anonymous = _app.CreateClient(); using var admin = _app.CreateClient();
        await _app.ProvisionServiceAdminAsync();
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/beta/service-admin/auth/login") { Content = JsonContent.Create(new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.ServiceAdminPassword }) };
        login.Headers.Add("X-CSRF-TOKEN", await TokenAsync(admin, "/api/beta/service-admin/auth"));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(login)).StatusCode);
        var id = Guid.NewGuid();
        // WHEN invoking every tenant route THEN both contexts are denied before input/tenant access.
        foreach (var client in new[] { anonymous, admin })
        {
            foreach (var path in new[] { "", "/" + id }) Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Route + path)).StatusCode);
            foreach (var (method, path) in Writes(id))
                Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(new HttpRequestMessage(method, Route + path) { Content = JsonContent.Create(new { }) })).StatusCode);
        }
        // AND an authenticated member cannot mutate any route without a valid antiforgery token.
        foreach (var (method, path) in Writes(id))
            Assert.Equal(HttpStatusCode.BadRequest, (await _member.SendAsync(new HttpRequestMessage(method, Route + path) { Content = JsonContent.Create(new { }) })).StatusCode);
        using var badToken = new HttpRequestMessage(HttpMethod.Post, Route + "/tenant-entries") { Content = JsonContent.Create(Addition()) };
        badToken.Headers.Add("X-CSRF-TOKEN", "invalid");
        Assert.Equal(HttpStatusCode.BadRequest, (await _member.SendAsync(badToken)).StatusCode);
    }
    private static IEnumerable<(HttpMethod, string)> Writes(Guid id) => [(HttpMethod.Post, "/tenant-entries"), (HttpMethod.Put, "/tenant-entries/" + id),
        (HttpMethod.Post, "/tenant-entries/" + id + "/archive"), (HttpMethod.Post, "/tenant-entries/" + id + "/restore"),
        (HttpMethod.Put, "/" + id + "/overrides"), (HttpMethod.Post, "/" + id + "/reset")];

    [Fact]
    public async Task MalformedTransportAndMissingVersionsReturnTypedValidation()
    {
        // GIVEN a member and malformed JSON/collections that must fail before a write transaction.
        var content = Addition(); var created = await CreateAsync(content);
        foreach (var (method, path, body, field) in new (HttpMethod, string, object, string)[]
        {
            (HttpMethod.Post, "/tenant-entries", content with { Sources = [null!] }, "sources"),
            (HttpMethod.Post, "/tenant-entries", content with { Aliases = [null!] }, "aliases"),
            (HttpMethod.Post, "/tenant-entries", content with { Sources = null! }, "sources"),
            (HttpMethod.Post, "/tenant-entries", content with { Sources = Enumerable.Repeat(GemReferenceSamples.Source("commonName"), 65).ToArray() }, "sources"),
            (HttpMethod.Post, "/tenant-entries", content with { Aliases = Enumerable.Repeat("alias", 65).ToArray() }, "aliases"),
            (HttpMethod.Put, "/tenant-entries/" + content.Id, new { content }, "effectiveVersion"),
            (HttpMethod.Put, "/tenant-entries/" + content.Id, new { content, effectiveVersion = new { tenantRowVersion = "bad" } }, "effectiveVersion"),
            (HttpMethod.Put, "/" + Guid.NewGuid() + "/overrides", new { overrides = new Dictionary<string, object?> { ["commonName"] = null }, effectiveVersion = new { } }, "overrides"),
            (HttpMethod.Put, "/tenant-entries/" + content.Id, new { content = (object?)null, effectiveVersion = created.EffectiveVersion }, "content"),
        })
        {
            // WHEN malformed fields are submitted THEN field errors are explicit and no server error occurs.
            var problem = await FailureAsync(method, path, body, HttpStatusCode.BadRequest, "validation_failed");
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
        }
        foreach (var malformed in new[] { "null", "{", "[]", "{\"id\":123}" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Route + "/tenant-entries") { Content = new StringContent(malformed, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-CSRF-TOKEN", await TokenAsync(_member)); using var response = await _member.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("validation_failed", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        Assert.Equal(created.EffectiveVersion, (await _member.GetFromJsonAsync<GemReferenceDetailResponse>(Route + "/" + content.Id))!.EffectiveVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedBodiesAreBoundedWithOrWithoutContentLength(bool chunked)
    {
        // GIVEN a signed-in member and a body larger than the reference write limit.
        var bytes = Encoding.UTF8.GetBytes("{\"commonName\":\"" + new string('a', 1024 * 1024) + "\"}");
        using var request = new HttpRequestMessage(HttpMethod.Post, Route + "/tenant-entries")
        { Content = chunked ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/json"); request.Headers.Add("X-CSRF-TOKEN", await TokenAsync(_member));
        // WHEN transport length is declared or absent THEN it returns a bounded private rejection.
        using var response = await _member.SendAsync(request); Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("request_too_large", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
