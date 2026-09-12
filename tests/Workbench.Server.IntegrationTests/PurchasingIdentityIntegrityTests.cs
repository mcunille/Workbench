// Copyright (c) 2026 The White Stag Collection.
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Purchasing;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;

namespace Workbench.Server.IntegrationTests;

public sealed partial class PurchasingIdentityEndpointTests
{
    [Fact]
    public async Task DraftSearchPaginationBindsNormalizedQueryAndTenant()
    {
        // GIVEN more matching purchases than fit on one page and an unrelated purchase.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        using var foreign = application.CreateClient();
        await LoginAsync(client);
        await LoginAsync(foreign, "other@example.com");
        var expected = new HashSet<Guid>();
        for (var index = 0; index < 51; index++)
            expected.Add((await DraftSave(client, Empty with { Title = $"Matching purchase {index}" })).DraftOrderId);
        await DraftSave(client, Empty with { Title = "Unrelated purchase" });

        // WHEN a normalized search advances through both pages THEN every match appears exactly once.
        var first = (await client.GetFromJsonAsync<DraftOrderPageResponseV2>(DraftPath + "?query=%20matching%20"))!;
        Assert.Equal(50, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        var cursor = Uri.EscapeDataString(first.NextCursor);
        var second = (await client.GetFromJsonAsync<DraftOrderPageResponseV2>(DraftPath + "?query=MATCHING&cursor=" + cursor))!;
        Assert.Single(second.Items);
        Assert.Null(second.NextCursor);
        var actual = first.Items.Concat(second.Items).Select(row => row.Id).ToArray();
        Assert.Equal(51, actual.Distinct().Count());
        Assert.True(expected.SetEquals(actual));

        // AND changing the query or business cannot reuse a continuation from the original search.
        await AssertInvalidIdentityCursor(client, DraftPath + "?query=unrelated&cursor=" + cursor);
        await AssertInvalidIdentityCursor(foreign, DraftPath + "?query=matching&cursor=" + cursor);
        Assert.Empty((await foreign.GetFromJsonAsync<DraftOrderPageResponseV2>(DraftPath + "?query=matching"))!.Items);
    }

    [Fact]
    public async Task SupplierSearchPaginationBindsQueryArchiveScopeAndTenant()
    {
        // GIVEN 51 active matching suppliers, an archived match, and another business.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        using var foreign = application.CreateClient();
        await LoginAsync(client);
        await LoginAsync(foreign, "other@example.com");
        var expected = new HashSet<Guid>();
        for (var index = 0; index < 51; index++)
            expected.Add((await SupplierSave(client, Contact with { Name = $"Directory match {index}" })).SupplierId);
        var archived = await SupplierSave(client, Contact with { Name = "Directory match archived" });
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(client, HttpMethod.Post,
            $"/api/suppliers/{archived.SupplierId}/archive",
            new ArchiveSupplierRequest(Guid.NewGuid(), archived.SavedVersion, true))).StatusCode);

        // WHEN paging active matches THEN the archived supplier is absent and every active match occurs once.
        var first = (await client.GetFromJsonAsync<SupplierPageResponse>("/api/suppliers?query=%20directory%20"))!;
        Assert.Equal(50, first.Items.Count);
        Assert.NotNull(first.NextCursor);
        var cursor = Uri.EscapeDataString(first.NextCursor);
        var second = (await client.GetFromJsonAsync<SupplierPageResponse>("/api/suppliers?query=DIRECTORY&cursor=" + cursor))!;
        Assert.Single(second.Items);
        Assert.Null(second.NextCursor);
        var actual = first.Items.Concat(second.Items).Select(row => row.Id).ToArray();
        Assert.Equal(51, actual.Distinct().Count());
        Assert.True(expected.SetEquals(actual));

        // AND changed query, archive scope or business requires restarting pagination.
        await AssertInvalidIdentityCursor(client, "/api/suppliers?query=other&cursor=" + cursor);
        await AssertInvalidIdentityCursor(client, "/api/suppliers?query=directory&includeArchived=true&cursor=" + cursor);
        await AssertInvalidIdentityCursor(foreign, "/api/suppliers?query=directory&cursor=" + cursor);
        Assert.Empty((await foreign.GetFromJsonAsync<SupplierPageResponse>("/api/suppliers?query=directory"))!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/suppliers/{archived.SupplierId}")).StatusCode);
    }

    private static async Task AssertInvalidIdentityCursor(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_cursor", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task UpgradeAndV1ReplayPreserveEveryStoredReceiptByte()
    {
        // GIVEN a successful V1 create and update with their original durable request evidence.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer, priorMigration: "AddDraftSupplierOrders");
        using var client = application.CreateClient();
        await LoginAsync(client);
        var create = new CreateDraftOrderRequest(Guid.NewGuid(), new("Before upgrade", "Supplier", null, null, [], []));
        using var created = await SendAsync(client, HttpMethod.Post, "/api/purchase-order-drafts", create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var receipt = (await created.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!;
        var update = new UpdateDraftOrderRequest(Guid.NewGuid(), receipt.SavedVersion, create.Draft with { Title = "Reviewed before upgrade" });
        using var updated = await SendAsync(client, HttpMethod.Put, $"/api/purchase-order-drafts/{receipt.DraftOrderId}", update);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        await using var admin = new SqlConnection(application.AdminConnectionString);
        await admin.OpenAsync();
        async Task<string> Evidence()
        {
            await using var read = new SqlCommand("SELECT (SELECT * FROM Purchasing.DraftOrderRequestReceipts ORDER BY RequestId FOR JSON PATH,INCLUDE_NULL_VALUES)", admin);
            return (string)(await read.ExecuteScalarAsync())!;
        }
        var before = await Evidence();

        // WHEN migration backfills references and both old commands are retried.
        await DatabaseMigrator.MigrateAsync(application.AdminConnectionString, default);
        using var replayCreate = await SendAsync(client, HttpMethod.Post, "/api/purchase-order-drafts", create);
        using var replayUpdate = await SendAsync(client, HttpMethod.Put, $"/api/purchase-order-drafts/{receipt.DraftOrderId}", update);
        Assert.Equal(HttpStatusCode.OK, replayCreate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayUpdate.StatusCode);
        Assert.True((await replayCreate.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!.Replayed);
        Assert.True((await replayUpdate.Content.ReadFromJsonAsync<SaveDraftOrderResponse>())!.Replayed);

        // THEN fingerprints, actors, original versions, completion times and all receipt columns are unchanged.
        Assert.Equal(before, await Evidence());
    }
}

public sealed partial class DraftOrderDatabaseTests
{
    [Fact]
    public async Task SupplierReplayByAuthorizedColleaguePreservesOriginalActorAndEvidence()
    {
        // GIVEN two enabled actors in one business and a supplier created by the first actor.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid();
        var originalActor = Guid.NewGuid();
        var colleague = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid());
        await SeedActor(database, tenant, originalActor);
        await SeedActor(database, tenant, colleague);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var request = Guid.NewGuid();
        var canonical = SupplierCanonical("Create", null, null, new("Reviewed supplier", null, null, null, null, null));
        var saved = await SaveSupplier(connection, originalActor, request, canonical);
        async Task<string> Evidence()
        {
            await using var read = new SqlCommand("SELECT (SELECT * FROM Purchasing.SupplierRequestReceipts ORDER BY RequestId FOR JSON PATH,INCLUDE_NULL_VALUES)", connection);
            return (string)(await read.ExecuteScalarAsync())!;
        }
        var before = await Evidence();

        // WHEN the currently authorized colleague resolves the identical request.
        var replay = await SaveSupplier(connection, colleague, request, canonical);

        // THEN replay returns the same identity/version without rewriting receipt or supplier attribution.
        Assert.True(replay.Replayed);
        Assert.Equal(saved.Id, replay.Id);
        Assert.Equal(saved.Version, replay.Version);
        Assert.Equal(before, await Evidence());
        await using var actors = new SqlCommand("SELECT COUNT(*) FROM Purchasing.Suppliers WHERE Id=@id AND CreatedByUserId=@actor AND UpdatedByUserId=@actor", connection);
        actors.Parameters.AddWithValue("@id", saved.Id);
        actors.Parameters.AddWithValue("@actor", originalActor);
        Assert.Equal(1, await actors.ExecuteScalarAsync());
    }
}
