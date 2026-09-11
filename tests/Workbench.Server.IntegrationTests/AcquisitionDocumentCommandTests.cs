// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Tenancy;
using Xunit;
namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class AcquisitionDocumentCommandTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task PendingReservationsBoundCapacityAndTenantScopedRequestsAreImmutable()
    {
        // GIVEN an active item linked to an acquisition and independent restricted connections.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var other = Guid.NewGuid(); var item = Guid.NewGuid(); var acquisition = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, other);
        await using var admin = new SqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        await using var seed = new SqlCommand("""
            INSERT Inventory.Items(Id,TenantId,TrackingKind,Name,CreatedAtUtc,CreationRequestId)
            VALUES(@item,@tenant,'Individual','Document specimen',SYSUTCDATETIME(),NEWID());
            INSERT Inventory.Acquisitions(Id,TenantId,Method,CreatedAtUtc,CreationRequestId)
            VALUES(@acquisition,@tenant,'Unknown',SYSUTCDATETIME(),NEWID());
            INSERT Inventory.AcquisitionItems(TenantId,ItemId,AcquisitionId) VALUES(@tenant,@item,@acquisition);
            SELECT i.RowVersion,a.RowVersion FROM Inventory.Items i CROSS JOIN Inventory.Acquisitions a WHERE i.Id=@item AND a.Id=@acquisition;
            """, admin);
        seed.Parameters.AddWithValue("@tenant", tenant); seed.Parameters.AddWithValue("@item", item); seed.Parameters.AddWithValue("@acquisition", acquisition);
        byte[] itemVersion, acquisitionVersion;
        await using (var reader = await seed.ExecuteReaderAsync()) { Assert.True(await reader.ReadAsync()); itemVersion = (byte[])reader[0]; acquisitionVersion = (byte[])reader[1]; }
        var connectionString = await database.CreateWebUserAsync();
        var proof = new TenantContextProof(await database.GetTenantContextProofKeyAsync());
        await using var first = new SqlConnection(connectionString); await first.OpenAsync(); await proof.ApplyAsync(first, tenant, default);
        await using var second = new SqlConnection(connectionString); await second.OpenAsync(); await proof.ApplyAsync(second, tenant, default);
        // WHEN malformed labels and noncanonical content types reach restricted SQL directly THEN they are rejected before reservation.
        foreach (var label in new[] { "\t\n\u2003", new string('x', 201) })
            Assert.Equal(50076, (await Assert.ThrowsAsync<SqlException>(() => Prepare(first, item, acquisition, Guid.NewGuid(), itemVersion, acquisitionVersion, label))).Number);
        foreach (var mediaType in new[] { "APPLICATION/PDF", "application/pdf ", "application/pdf" + new string(' ', 200) })
            Assert.Equal(50076, (await Assert.ThrowsAsync<SqlException>(() => Prepare(first, item, acquisition, Guid.NewGuid(), itemVersion, acquisitionVersion, mediaType: mediaType))).Number);
        var request = Guid.NewGuid();
        // WHEN retrying identical evidence THEN only one pending reservation exists.
        await Prepare(first, item, acquisition, request, itemVersion, acquisitionVersion);
        await Prepare(second, item, acquisition, request, itemVersion, acquisitionVersion);
        // AND reuse with a changed label conflicts rather than replacing immutable evidence.
        Assert.Equal(50077, (await Assert.ThrowsAsync<SqlException>(() => Prepare(second, item, acquisition, request, itemVersion, acquisitionVersion, "Different"))).Number);
        for (var n = 1; n < 19; n++) await Prepare(first, item, acquisition, Guid.NewGuid(), itemVersion, acquisitionVersion);
        // WHEN independent connections compete for the last slot THEN precisely one reservation wins.
        async Task<bool> Attempt(SqlConnection connection)
        {
            try { await Prepare(connection, item, acquisition, Guid.NewGuid(), itemVersion, acquisitionVersion); return true; }
            catch (SqlException error) when (error.Number == 50079) { return false; }
        }
        var winners = await Task.WhenAll(Attempt(first), Attempt(second));
        Assert.Single(winners, value => value);
        await using var count = new SqlCommand("SELECT COUNT(*) FROM Inventory.AcquisitionDocumentOperations", first);
        Assert.Equal(20, await count.ExecuteScalarAsync());
        await using var visible = new SqlCommand("SELECT COUNT(*) FROM Inventory.AcquisitionDocuments", first);
        Assert.Equal(0, await visible.ExecuteScalarAsync());
        // WHEN publication finalizes the first reservation THEN it becomes visible and advances both versions.
        await Finish(first, request);
        await using var saved = new SqlCommand("SELECT COUNT(*) FROM Inventory.AcquisitionDocuments WHERE RemovedAtUtc IS NULL", first);
        Assert.Equal(1, await saved.ExecuteScalarAsync());
        await using var result = new SqlCommand("SELECT State,ResultItemVersion,ResultAcquisitionVersion FROM Inventory.AcquisitionDocumentOperations WHERE RequestId=@request", first);
        result.Parameters.AddWithValue("@request", request);
        await using (var reader = await result.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync()); Assert.Equal(1, reader.GetInt32(0));
            Assert.NotEqual(itemVersion, (byte[])reader[1]); Assert.NotEqual(acquisitionVersion, (byte[])reader[2]);
        }
        // WHEN another already-published reservation loses that version race THEN its bytes enter retention and capacity is released.
        await using var pending = new SqlCommand("SELECT TOP(1) RequestId FROM Inventory.AcquisitionDocumentOperations WHERE State=0", first);
        var stale = (Guid)(await pending.ExecuteScalarAsync())!;
        await Finish(second, stale);
        await using var conflict = new SqlCommand("SELECT State FROM Inventory.AcquisitionDocumentOperations WHERE RequestId=@request", first);
        conflict.Parameters.AddWithValue("@request", stale); Assert.Equal(2, await conflict.ExecuteScalarAsync());
        await using var retention = new SqlCommand("SELECT COUNT(*) FROM Operations.WorkItems WHERE Kind=1", first);
        Assert.Equal(1, await retention.ExecuteScalarAsync());
        // WHEN an operator definitively reconciles an unpublished reservation as failed THEN capacity releases and replay evidence remains.
        await using var reconciled = new SqlCommand("UPDATE Storage.Revisions SET State=2 WHERE Id=(SELECT TOP(1) RevisionId FROM Inventory.AcquisitionDocumentOperations WHERE State=0)", admin);
        await reconciled.ExecuteNonQueryAsync();
        await using var failures = new SqlCommand("SELECT COUNT(*) FROM Inventory.AcquisitionDocumentOperations WHERE State=2", first);
        Assert.Equal(2, await failures.ExecuteScalarAsync());
        // AND the narrow maintenance role cannot grant itself direct mutation of document command evidence.
        await using (var maintenance = new SqlConnection(await database.CreateRoleUserAsync("workbench_storage_maintenance")))
        {
            await maintenance.OpenAsync();
            await using var denied = new SqlCommand("UPDATE Inventory.AcquisitionDocumentOperations SET State=2", maintenance);
            Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => denied.ExecuteNonQueryAsync())).Number);
        }
        // AND committed replay remains available after archive without another document or another retention job.
        await using var archive = new SqlCommand("UPDATE Inventory.Items SET ArchivedAtUtc=SYSUTCDATETIME() WHERE Id=@item", admin);
        archive.Parameters.AddWithValue("@item", item); await archive.ExecuteNonQueryAsync();
        await Prepare(first, item, acquisition, request, itemVersion, acquisitionVersion);
        await Finish(first, request);
        Assert.Equal(1, await saved.ExecuteScalarAsync()); Assert.Equal(1, await retention.ExecuteScalarAsync());
        // AND a foreign tenant sees no metadata and cannot use the item relationship.
        await using var foreign = new SqlConnection(connectionString);
        await foreign.OpenAsync();
        await proof.ApplyAsync(foreign, other, default);
        await using var hidden = new SqlCommand("SELECT COUNT(*) FROM Inventory.AcquisitionDocumentOperations", foreign);
        Assert.Equal(0, await hidden.ExecuteScalarAsync());
        Assert.Equal(50078, (await Assert.ThrowsAsync<SqlException>(() => Prepare(foreign, item, acquisition, Guid.NewGuid(), itemVersion, acquisitionVersion))).Number);
    }
    private static async Task Finish(SqlConnection connection, Guid request)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = new SqlCommand("Inventory.FinishAcquisitionDocument", connection, transaction) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@RequestId", request); command.Parameters.AddWithValue("@Published", true);
        await command.ExecuteNonQueryAsync(); await transaction.CommitAsync();
    }
    private static async Task Prepare(SqlConnection connection, Guid item, Guid acquisition, Guid request, byte[] itemVersion, byte[] acquisitionVersion, string label = "Receipt", string mediaType = "application/pdf")
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = new SqlCommand("Inventory.PrepareAcquisitionDocument", connection, transaction) { CommandType = CommandType.StoredProcedure };
        foreach (var (name, value) in new (string, object)[] { ("@ItemId", item), ("@AcquisitionId", acquisition), ("@RequestId", request), ("@ExpectedItemVersion", itemVersion), ("@ExpectedAcquisitionVersion", acquisitionVersion), ("@Kind", 0), ("@Label", label), ("@MediaType", mediaType), ("@Extension", "pdf"), ("@Length", 12L), ("@Sha256", new string('A', 64)), ("@ProviderAlias", "test"), ("@ActorUserId", Guid.NewGuid()) }) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(); await transaction.CommitAsync();
    }
}
