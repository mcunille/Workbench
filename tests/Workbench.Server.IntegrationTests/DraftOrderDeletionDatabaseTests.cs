// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Xunit;
namespace Workbench.Server.IntegrationTests;

public sealed partial class DraftOrderDatabaseTests
{
    [Fact]
    public async Task DeleteClearsDraftContentAndReplaysWithoutResurrectingIt()
    {
        // GIVEN a saved shopping list containing private business fields.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid(); var createRequest = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var canonical = JsonSerializer.Serialize(new
        {
            operation = "Create",
            targetId = (string?)null,
            expectedVersion = (string?)null,
            draft = new
            {
                title = "Cart",
                supplierName = "Supplier",
                supplierId = (Guid?)null,
                supplierContactName = (string?)null,
                supplierEmail = (string?)null,
                supplierPhone = (string?)null,
                supplierWebsite = (string?)null,
                supplierPostalAddress = (string?)null,
                supplierOrderReference = (string?)null,
                platform = (string?)null,
                currency = "USD",
                notes = "Private notes",
                sourceLinks = new[] { "https://supplier.example/cart" },
                entries = new[] { new { id = Guid.NewGuid(), description = "Stone", notes = "Entry notes", sourceLink = "https://supplier.example/stone", indicativePrice = "1.0000" } }
            },
        });
        var saved = await Save(connection, actor, createRequest, canonical, "Create"); var request = Guid.NewGuid();
        // WHEN deletion succeeds THEN it advances the version, clears content and retains only a tombstone and receipts.
        var deleted = await Delete(connection, actor, request, saved.Id, saved.Version);
        Assert.False(deleted.Replayed); Assert.NotEqual(saved.Version, deleted.Version);
        await using var read = new SqlCommand("""
            SELECT COUNT(*) FROM Purchasing.DraftOrders WHERE Id=@id AND IsDeleted=1
                AND Title IS NULL AND SupplierName IS NULL AND Currency IS NULL AND Notes IS NULL
                AND ContentJson=N'{"sourceLinks":[],"entries":[]}' AND UpdatedByUserId=@actor
            """, connection);
        read.Parameters.AddWithValue("@id", saved.Id); read.Parameters.AddWithValue("@actor", actor);
        Assert.Equal(1, await read.ExecuteScalarAsync());
        // AND exact deletion retries return original evidence without another mutation.
        var replay = await Delete(connection, actor, request, saved.Id, saved.Version);
        Assert.True(replay.Replayed); Assert.Equal(deleted.Version, replay.Version); Assert.Equal(deleted.Completed, replay.Completed);
        Assert.Equal(50404, (await Assert.ThrowsAsync<SqlException>(() => Delete(connection, actor, Guid.NewGuid(), saved.Id, deleted.Version))).Number);
        Assert.Equal(50404, (await Assert.ThrowsAsync<SqlException>(() => Save(connection, actor, Guid.NewGuid(), Canonical("Update", saved.Id, deleted.Version, "Resurrected"), "Update"))).Number);
        // AND an old creation receipt can confirm that creation once succeeded, but cannot resurrect content.
        Assert.True((await Save(connection, actor, createRequest, canonical, "Create")).Replayed);
        Assert.Equal(1, await read.ExecuteScalarAsync());
    }

    [Fact]
    public async Task DeleteChecksVersionTenantActorAndRequestIdentity()
    {
        // GIVEN saved drafts in separate tenant contexts.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var foreign = Guid.NewGuid(); var actor = Guid.NewGuid(); var otherActor = Guid.NewGuid(); var foreignActor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, foreign);
        await SeedActor(database, tenant, actor); await SeedActor(database, tenant, otherActor); await SeedActor(database, foreign, foreignActor);
        var web = await database.CreateWebUserAsync(); await using var connection = await Open(database, web, tenant); await using var foreignConnection = await Open(database, web, foreign);
        var saved = await Save(connection, actor, Guid.NewGuid(), Canonical("Create", null, null, "Draft"), "Create");
        var edited = await Save(connection, actor, Guid.NewGuid(), Canonical("Update", saved.Id, saved.Version, "Edited"), "Update");
        // WHEN deleting a stale version or a foreign target THEN SQL rejects without clearing the current draft.
        Assert.Equal(50409, (await Assert.ThrowsAsync<SqlException>(() => Delete(connection, actor, Guid.NewGuid(), saved.Id, saved.Version))).Number);
        Assert.Equal(50404, (await Assert.ThrowsAsync<SqlException>(() => Delete(foreignConnection, foreignActor, Guid.NewGuid(), saved.Id, edited.Version))).Number);
        foreach (var version in new[] { new byte[7], new byte[9] })
            Assert.Equal(50400, (await Assert.ThrowsAsync<SqlException>(() => Delete(connection, actor, Guid.NewGuid(), saved.Id, version))).Number);
        var request = Guid.NewGuid(); var deleted = await Delete(connection, actor, request, saved.Id, edited.Version);
        // AND changing the version or original actor under that request identifier cannot replay its receipt.
        Assert.Equal(50410, (await Assert.ThrowsAsync<SqlException>(() => Delete(connection, actor, request, saved.Id, saved.Version))).Number);
        Assert.Equal(50410, (await Assert.ThrowsAsync<SqlException>(() => Delete(connection, otherActor, request, saved.Id, edited.Version))).Number);
        Assert.Equal(deleted.Version, (await Delete(connection, actor, request, saved.Id, edited.Version)).Version);
        // AND disabling the actor blocks even a previously successful exact deletion retry.
        await using var admin = new SqlConnection(database.AdminConnectionString); await admin.OpenAsync();
        await using var disable = new SqlCommand("UPDATE [Identity].[Users] SET State=2 WHERE Id=@actor", admin);
        disable.Parameters.AddWithValue("@actor", actor); await disable.ExecuteNonQueryAsync();
        Assert.Equal(50403, (await Assert.ThrowsAsync<SqlException>(() => Delete(connection, actor, request, saved.Id, edited.Version))).Number);
    }

    [Fact]
    public async Task RolledBackDeletionRetainsOriginalContentAndCreatesNoReceipt()
    {
        // GIVEN a saved draft and an enclosing transaction that will be rolled back.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid(); await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var saved = await Save(connection, actor, Guid.NewGuid(), Canonical("Create", null, null, "Retained"), "Create"); var request = Guid.NewGuid();
        await using (var begin = new SqlCommand("BEGIN TRANSACTION", connection)) await begin.ExecuteNonQueryAsync();
        await Delete(connection, actor, request, saved.Id, saved.Version);
        // WHEN the outer operation fails THEN deletion state and receipt evidence roll back atomically.
        await using (var rollback = new SqlCommand("ROLLBACK", connection)) await rollback.ExecuteNonQueryAsync();
        await using var read = new SqlCommand("SELECT COUNT(*) FROM Purchasing.DraftOrders WHERE Id=@id AND IsDeleted=0 AND Title=N'Retained' AND RowVersion=@version", connection);
        read.Parameters.AddWithValue("@id", saved.Id); read.Parameters.AddWithValue("@version", saved.Version); Assert.Equal(1, await read.ExecuteScalarAsync());
        await using var receipt = new SqlCommand("SELECT COUNT(*) FROM Purchasing.DraftOrderRequestReceipts WHERE RequestId=@request", connection);
        receipt.Parameters.AddWithValue("@request", request); Assert.Equal(0, await receipt.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CompetingDeletionRetriesMutateOnceAndCompetingEditCannotResurrect()
    {
        // GIVEN two connections to the same saved draft.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid(); await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        var web = await database.CreateWebUserAsync(); await using var a = await Open(database, web, tenant); await using var b = await Open(database, web, tenant);
        var saved = await Save(a, actor, Guid.NewGuid(), Canonical("Create", null, null, "Draft"), "Create"); var request = Guid.NewGuid();
        // WHEN exact deletion retries compete THEN one deletes and the other replays the same version.
        var results = await Task.WhenAll(Delete(a, actor, request, saved.Id, saved.Version), Delete(b, actor, request, saved.Id, saved.Version));
        Assert.Single(results, row => row.Replayed); Assert.Equal(results[0].Version, results[1].Version);
        var competing = await Save(a, actor, Guid.NewGuid(), Canonical("Create", null, null, "Competing"), "Create");
        async Task<int> Edit()
        {
            try { await Save(a, actor, Guid.NewGuid(), Canonical("Update", competing.Id, competing.Version, "Edited"), "Update"); return 0; }
            catch (SqlException error) { return error.Number; }
        }
        async Task<int> Remove()
        {
            try { await Delete(b, actor, Guid.NewGuid(), competing.Id, competing.Version); return 0; }
            catch (SqlException error) { return error.Number; }
        }
        // WHEN editing races deletion THEN exactly one operation succeeds; the loser sees missing/conflicting current state.
        var outcomes = await Task.WhenAll(Edit(), Remove());
        Assert.Single(outcomes, result => result == 0); Assert.Contains(outcomes, result => result is 50404 or 50409);
    }

    private static async Task<(Guid Id, byte[] Version, DateTimeOffset Completed, bool Replayed)> Delete(SqlConnection connection, Guid actor, Guid request, Guid id, byte[] version)
    {
        await using var command = new SqlCommand("Purchasing.DeleteDraftOrder", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@RequestId", request); command.Parameters.AddWithValue("@ActorUserId", actor); command.Parameters.AddWithValue("@DraftOrderId", id);
        command.Parameters.Add("@ExpectedRowVersion", SqlDbType.VarBinary, -1).Value = version;
        await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        return (reader.GetGuid(reader.GetOrdinal("DraftOrderId")), (byte[])reader["SavedVersion"], reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("CompletedAtUtc")), reader.GetBoolean(reader.GetOrdinal("Replayed")));
    }
}
