// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Workbench.Server.Gemology;
using Workbench.Server.Identity;
using Xunit;

namespace Workbench.Server.IntegrationTests.Infrastructure;

internal static class GemReferenceRecoveryEvidence
{
    internal static async Task SeedAsync(AuthTestApplication application)
    {
        // GIVEN published claim children, a durable publication receipt/audit, and an unpublished draft.
        var session = await GemReferenceDraftTests.CreateSessionAsync(application);
        var drafts = new GemReferenceDraftService(application.WebConnectionString);
        var localitySource = GemReferenceSamples.Source("notableLocality");
        var content = GemReferenceSamples.Mineral() with
        {
            Id = Guid.NewGuid(),
            CommonName = "Synthetic recovery reference",
            Aliases = ["Recovery alias"],
            Sources = [.. GemReferenceSamples.Mineral().Sources, GemReferenceSamples.Source("aliases"), localitySource],
            NotableLocality = new("Synthetic place", "Synthetic recovery claim", localitySource.ReviewedOn, localitySource.Id),
        };
        var draft = await drafts.SaveAsync(AuthTestApplication.ServiceAdminId, session, Guid.NewGuid(), new(content.Id, content, null, null), default);
        var outcome = await new GemReferencePublicationService(application.WebConnectionString).PublishAsync(
            AuthTestApplication.ServiceAdminId, session, new(Guid.NewGuid(), [new(draft.Id, draft.RowVersion)]), default);
        Assert.Equal("published", outcome.Code);
        var pending = content with { Id = Guid.NewGuid(), CommonName = "Unpublished recovery draft" };
        await drafts.SaveAsync(AuthTestApplication.ServiceAdminId, session, Guid.NewGuid(), new(pending.Id, pending, null, null), default);

        // AND tenant-authored sourced content, an archived addition, and a sparse override created through protected APIs.
        using var tenant = application.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await RecoveryTests.PostWithAntiforgeryAsync(tenant, "/api/beta/auth/login",
            new { email = AuthTestApplication.AdminEmail, password = AuthTestApplication.AdminPassword })).StatusCode);
        var addition = content with { Id = Guid.NewGuid(), CommonName = "Private recovery reference" };
        var response = await SendAsync(tenant, HttpMethod.Post, "/tenant-entries", addition);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = (await response.Content.ReadFromJsonAsync<GemReferenceDetailResponse>())!;
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(tenant, HttpMethod.Post, $"/tenant-entries/{addition.Id}/archive",
            new GemReferenceTenantVersionRequest(saved.EffectiveVersion!))).StatusCode);
        var shared = (await tenant.GetFromJsonAsync<GemReferenceDetailResponse>($"/api/beta/gem-reference/{content.Id}?origin=workbench"))!;
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(tenant, HttpMethod.Put, $"/{content.Id}/overrides",
            new GemReferenceOverridesRequest(new Dictionary<string, GemReferenceFieldOverride>
            {
                ["description"] = new("replace", JsonSerializer.SerializeToElement("Retained tenant recovery note"), []),
            }, shared.EffectiveVersion!))).StatusCode);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object body)
    {
        using var request = new HttpRequestMessage(method, "/api/beta/gem-reference" + path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", (await client.GetFromJsonAsync<AntiforgeryResponse>("/api/beta/auth/antiforgery"))!.RequestToken);
        return await client.SendAsync(request);
    }

    internal static Task<string> SnapshotAsync(string connection) => ServiceAdminRestoreTests.ScalarAsync<string>(connection, """
        SELECT (SELECT (SELECT * FROM Gemology.Entries ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES) Entries,
            (SELECT * FROM Gemology.Aliases ORDER BY EntryId,Position FOR JSON PATH,INCLUDE_NULL_VALUES) Aliases,
            (SELECT * FROM Gemology.SourceAssertions ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES) Sources,
            (SELECT * FROM Gemology.LocalityAssertions ORDER BY EntryId FOR JSON PATH,INCLUDE_NULL_VALUES) Localities,
            (SELECT * FROM Gemology.TenantEntries ORDER BY TenantId,Id FOR JSON PATH,INCLUDE_NULL_VALUES) TenantEntries,
            (SELECT * FROM Gemology.TenantOverrides ORDER BY TenantId,EntryId FOR JSON PATH,INCLUDE_NULL_VALUES) Overrides,
            (SELECT * FROM Gemology.Drafts ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES) Drafts,
            (SELECT * FROM Gemology.PublishRequests ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES) Receipts,
            (SELECT * FROM Gemology.PublicationAudit ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES) Audit
        FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)
        """);
}
