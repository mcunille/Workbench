// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Inventory;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;
using static Workbench.Server.IntegrationTests.SharedAcquisitionBehaviorTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SharedAcquisitionConcurrencyTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData("competing-target")]
    [InlineData("duplicate")]
    [InlineData("archive")]
    [InlineData("shared-edit")]
    [InlineData("opposite-replacement")]
    public async Task IndependentSqlSessionsResolveRacesWithoutPartialLinks(string scenario)
    {
        // GIVEN independent authenticated requests and two original linked pieces.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var first = app.CreateClient();
        using var second = app.CreateClient();
        await LoginAsync(first);
        await LoginAsync(second);
        var a = await CreateItemAsync(first);
        var b = await CreateItemAsync(first);
        var contextA = await CreateContext(first, a, "A");
        var contextB = await CreateContext(first, b, "B");
        var request = Link(contextA, contextB.Acquisition);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> Replace()
        {
            await gate.Task;
            return await SendAsync(first, HttpMethod.Put, $"/api/items/{a.Id}/acquisition-link", request);
        }
        async Task<HttpResponseMessage> Compete()
        {
            await gate.Task;
            return scenario switch
            {
                "archive" => await SendAsync(second, HttpMethod.Post, $"/api/items/{a.Id}/archive", new { expectedVersion = contextA.ItemVersion }),
                "shared-edit" => await SendAsync(second, HttpMethod.Put, $"/api/items/{b.Id}/acquisition/{contextB.Acquisition!.Id}",
                    new UpdateAcquisitionRequest(contextB.ItemVersion, contextB.Acquisition.Version, "Trade", "Edited", null, null, null, null)),
                "opposite-replacement" => await SendAsync(second, HttpMethod.Put, $"/api/items/{b.Id}/acquisition-link", Link(contextB, contextA.Acquisition)),
                "duplicate" => await SendAsync(second, HttpMethod.Put, $"/api/items/{a.Id}/acquisition-link", request),
                _ => await SendAsync(second, HttpMethod.Put, $"/api/items/{a.Id}/acquisition-link", Link(contextA, null)),
            };
        }
        // WHEN commands race on real transactional SQL sessions THEN one succeeds and one gets a recoverable conflict.
        var replace = Replace();
        var compete = Compete();
        gate.SetResult();
        var outcomes = await Task.WhenAll(replace, compete);
        Assert.Single(outcomes, row => row.StatusCode == HttpStatusCode.OK);
        Assert.Single(outcomes, row => row.StatusCode == HttpStatusCode.Conflict);
        var currentA = await Current(first, a.Id);
        var currentB = await Current(first, b.Id);
        if (outcomes[0].StatusCode == HttpStatusCode.OK)
        {
            Assert.Equal(contextB.Acquisition!.Id, currentA.Acquisition!.Id);
            Assert.Equal(contextB.Acquisition.Id, currentB.Acquisition!.Id);
        }
        else if (scenario == "opposite-replacement")
        {
            Assert.Equal(contextA.Acquisition!.Id, currentA.Acquisition!.Id);
            Assert.Equal(contextA.Acquisition.Id, currentB.Acquisition!.Id);
        }
        else if (scenario == "competing-target") Assert.Null(currentA.Acquisition);
        else Assert.Equal(scenario == "duplicate" ? contextB.Acquisition!.Id : contextA.Acquisition!.Id, currentA.Acquisition!.Id);
        // AND both records and contexts survive, and a stale retry cannot silently rebase.
        Assert.Equal(2, (await first.GetFromJsonAsync<AcquisitionPageResponse>("/api/acquisitions"))!.Items.Count);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(first, HttpMethod.Put, $"/api/items/{a.Id}/acquisition-link", request)).StatusCode);
    }

    [Fact]
    public async Task LinkValidationAndForeignIdentifiersCannotExposeOrChangeRelatedState()
    {
        // GIVEN one owner context and one foreign context.
        await using var app = await AuthTestApplication.CreateAsync(sqlServer);
        using var owner = app.CreateClient();
        using var foreign = app.CreateClient();
        using var anonymous = app.CreateClient();
        await LoginAsync(owner);
        await LoginAsync(foreign, "other@example.com");
        var item = await CreateItemAsync(owner);
        var otherItem = await CreateItemAsync(foreign);
        var saved = await CreateContext(owner, item);
        var other = await CreateContext(foreign, otherItem);
        var valid = Link(saved, null);
        var path = $"/api/items/{item.Id}/acquisition-link";
        // WHEN anonymous or malformed requests arrive THEN they are rejected before mutation.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(path, valid)).StatusCode);
        foreach (var invalid in new object[] { valid with { ExpectedItemVersion = "AQ==" }, valid with { ExpectedAcquisitionVersion = null },
            valid with { ExpectedAcquisitionId = null }, valid with { ExpectedAcquisitionId = Guid.Empty },
            valid with { TargetAcquisitionId = saved.Acquisition!.Id }, valid with { TargetAcquisitionVersion = saved.Acquisition.Version },
            new { valid.ExpectedItemVersion, tenantId = Guid.NewGuid() } })
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(owner, HttpMethod.Put, path, invalid)).StatusCode);
        // WHEN missing or foreign identifiers are supplied, including an old link, THEN indistinguishable 404s hide context.
        foreach (var id in new[] { other.Acquisition!.Id, Guid.NewGuid() })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(owner, HttpMethod.Put, path, valid with { TargetAcquisitionId = id, TargetAcquisitionVersion = saved.Acquisition.Version })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(owner, HttpMethod.Put, path, valid with { ExpectedAcquisitionId = id })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(foreign, HttpMethod.Put, path, valid)).StatusCode);
        Assert.Equal(saved, await Current(owner, item.Id));
    }
}
