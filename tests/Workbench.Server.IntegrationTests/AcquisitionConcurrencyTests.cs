// Copyright (c) 2026 The White Stag Collection.

using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Inventory;
using Xunit;
using static Workbench.Server.IntegrationTests.AcquisitionEndpointTests;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class AcquisitionConcurrencyTests(SqlServerFixture sqlServer)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConcurrentCreationCommitsOneCompleteOrigin(bool sameRequest, bool differentItem)
    {
        // GIVEN two independent authenticated sessions and original saved item versions.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var first = application.CreateClient();
        using var second = application.CreateClient();
        await LoginAsync(first);
        await LoginAsync(second);
        var item = await CreateItemAsync(first);
        var otherItem = differentItem ? await CreateItemAsync(first) : item;
        var request = new CreateAcquisitionRequest(Guid.NewGuid(), item.Version, "Gift", null, null, null, null, null);
        var otherRequest = request with { CreationRequestId = sameRequest ? request.CreationRequestId : Guid.NewGuid(), ExpectedItemVersion = otherItem.Version };
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> CreateAsync(HttpClient client, Guid id, CreateAcquisitionRequest body)
        {
            await gate.Task;
            return await SendAsync(client, HttpMethod.Post, $"/api/items/{id}/acquisition", body);
        }
        // WHEN simultaneous writes use the same or competing request identifiers.
        var a = CreateAsync(first, item.Id, request);
        var b = CreateAsync(second, otherItem.Id, otherRequest);
        gate.SetResult();
        var results = await Task.WhenAll(a, b);
        // THEN exactly one creation commits and its replay succeeds only for the same original item.
        Assert.Single(results, result => result.StatusCode == HttpStatusCode.Created);
        Assert.Single(results, result => result.StatusCode == (sameRequest && !differentItem ? HttpStatusCode.OK : HttpStatusCode.Conflict));
        var winner = (await results.Single(result => result.StatusCode == HttpStatusCode.Created).Content.ReadFromJsonAsync<ItemAcquisitionResponse>())!;
        var firstSaved = (await first.GetFromJsonAsync<ItemAcquisitionResponse>($"/api/items/{item.Id}/acquisition"))!;
        var secondSaved = (await first.GetFromJsonAsync<ItemAcquisitionResponse>($"/api/items/{otherItem.Id}/acquisition"))!;
        Assert.True(winner == firstSaved || winner == secondSaved);
        if (differentItem) Assert.Single(new[] { firstSaved, secondSaved }, saved => saved.Acquisition is not null);
        // AND no orphan, duplicate link or partial replay evidence survives.
        await using var sql = new SqlConnection(application.AdminConnectionString);
        await sql.OpenAsync();
        foreach (var table in new[] { "Acquisitions", "AcquisitionItems", "AcquisitionCreationRecords" })
        {
            await using var count = new SqlCommand($"SELECT COUNT(*) FROM Inventory.{table}", sql);
            Assert.Equal(1, await count.ExecuteScalarAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditRacesHaveOneWinnerAndNeverOverwriteArchivedContext(bool archive)
    {
        // GIVEN independent sessions sharing the current item and acquisition tokens.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var first = application.CreateClient();
        using var second = application.CreateClient();
        await LoginAsync(first);
        await LoginAsync(second);
        var item = await CreateItemAsync(first);
        var path = $"/api/items/{item.Id}/acquisition";
        var created = await SendAsync(first, HttpMethod.Post, path,
            new CreateAcquisitionRequest(Guid.NewGuid(), item.Version, "Gift", null, null, null, null, null));
        var saved = (await created.Content.ReadFromJsonAsync<ItemAcquisitionResponse>())!;
        var editPath = $"{path}/{saved.Acquisition!.Id}";
        var update = new UpdateAcquisitionRequest(saved.ItemVersion, saved.Acquisition.Version, "Trade", "First", null, null, null, null);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> EditAsync() { await gate.Task; return await SendAsync(first, HttpMethod.Put, editPath, update); }
        async Task<HttpResponseMessage> CompeteAsync()
        {
            await gate.Task;
            return archive ? await SendAsync(second, HttpMethod.Post, $"/api/items/{item.Id}/archive", new { expectedVersion = saved.ItemVersion })
                : await SendAsync(second, HttpMethod.Put, editPath, update with { Source = "Second" });
        }
        // WHEN edit competes with another edit or archive over real SQL transactions.
        var edit = EditAsync();
        var competing = CompeteAsync();
        gate.SetResult();
        var results = await Task.WhenAll(edit, competing);
        // THEN one checked write commits and the rejected stale write cannot overwrite it.
        Assert.Single(results, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, response => response.StatusCode == HttpStatusCode.Conflict);
        var current = (await first.GetFromJsonAsync<ItemAcquisitionResponse>(path))!;
        var currentItem = (await first.GetFromJsonAsync<ItemDetailResponse>($"/api/items/{item.Id}"))!;
        Assert.Equal(currentItem.Version, current.ItemVersion);
        Assert.NotEqual(saved.ItemVersion, current.ItemVersion);
        if (archive && results[1].StatusCode == HttpStatusCode.OK)
        {
            Assert.NotNull(currentItem.ArchivedAtUtc);
            Assert.Equal(saved.Acquisition, current.Acquisition);
            Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(first, HttpMethod.Put, editPath, update with { ExpectedItemVersion = current.ItemVersion })).StatusCode);
        }
        else
        {
            Assert.Null(currentItem.ArchivedAtUtc);
            Assert.NotEqual(saved.Acquisition.Version, current.Acquisition!.Version);
            Assert.Equal("Trade", current.Acquisition.Method);
            Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(first, HttpMethod.Put, editPath, update)).StatusCode);
        }
    }

    [Fact]
    public async Task FailedReplayEvidenceInsertionRollsBackAllRowsAndItemVersion()
    {
        // GIVEN a database failure after the acquisition and link insert but before replay evidence.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        using var client = application.CreateClient();
        await LoginAsync(client);
        var item = await CreateItemAsync(client);
        await using var sql = new SqlConnection(application.AdminConnectionString);
        await sql.OpenAsync();
        await using var fail = new SqlCommand("ALTER TABLE Inventory.AcquisitionCreationRecords ADD CONSTRAINT CK_InjectedCreationFailure CHECK (Notes IS NOT NULL);", sql);
        await fail.ExecuteNonQueryAsync();
        var request = new CreateAcquisitionRequest(Guid.NewGuid(), item.Version, "Gift", null, null, null, null, null);
        // WHEN a transaction fails THEN it leaves no rows or changed item token.
        Assert.Equal(HttpStatusCode.InternalServerError, (await SendAsync(client, HttpMethod.Post, $"/api/items/{item.Id}/acquisition", request)).StatusCode);
        Assert.Equal(item, await client.GetFromJsonAsync<ItemDetailResponse>($"/api/items/{item.Id}"));
        // AND even a direct SQL caller catching the failure cannot commit partial acquisition/link rows.
        await using (var attemptedPartialCommit = new SqlCommand("""
            BEGIN TRANSACTION;
            BEGIN TRY
                EXEC Inventory.CreateAcquisition @ItemId=@id,@CreationRequestId=@request,@ExpectedItemVersion=@version,@Method=N'Gift';
            END TRY
            BEGIN CATCH
                SELECT XACT_STATE();
            END CATCH;
            IF @@TRANCOUNT>0 ROLLBACK;
            """, sql))
        {
            attemptedPartialCommit.Parameters.AddWithValue("@id", item.Id);
            attemptedPartialCommit.Parameters.AddWithValue("@request", request.CreationRequestId);
            attemptedPartialCommit.Parameters.AddWithValue("@version", Convert.FromBase64String(item.Version));
            Assert.Equal(-1, Convert.ToInt32(await attemptedPartialCommit.ExecuteScalarAsync()));
        }
        foreach (var table in new[] { "Acquisitions", "AcquisitionItems", "AcquisitionCreationRecords" })
        {
            await using var count = new SqlCommand($"SELECT COUNT(*) FROM Inventory.{table}", sql);
            Assert.Equal(0, await count.ExecuteScalarAsync());
        }
        await using var recover = new SqlCommand("ALTER TABLE Inventory.AcquisitionCreationRecords DROP CONSTRAINT CK_InjectedCreationFailure", sql);
        await recover.ExecuteNonQueryAsync();
        // AND the original request is safe to retry after recovery.
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(client, HttpMethod.Post, $"/api/items/{item.Id}/acquisition", request)).StatusCode);
    }
}
