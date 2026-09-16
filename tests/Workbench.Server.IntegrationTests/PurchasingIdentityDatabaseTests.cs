// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Purchasing;
using Xunit;
namespace Workbench.Server.IntegrationTests;

public sealed partial class DraftOrderDatabaseTests
{
    private static string SupplierCanonical(string operation, Guid? id, byte[]? version, SupplierContent? supplier, bool? isArchived = null) => JsonSerializer.Serialize(new { operation, targetId = id, expectedVersion = version is null ? null : Convert.ToBase64String(version), supplier, isArchived }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static async Task<(Guid Id, byte[] Version, bool Replayed)> SaveSupplier(SqlConnection connection, Guid actor, Guid request, string canonical)
    {
        await using var command = new SqlCommand("Purchasing.SaveSupplier", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@RequestId", request); command.Parameters.AddWithValue("@ActorUserId", actor); command.Parameters.AddWithValue("@CanonicalInputJson", canonical);
        await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        return (reader.GetGuid(reader.GetOrdinal("SupplierId")), (byte[])reader["SavedVersion"], reader.GetBoolean(reader.GetOrdinal("Replayed")));
    }
    private static DraftContentV2 Snapshot(Guid? supplier = null) => new(null, "Snapshot", null, null, [], [], supplier, null, "snapshot@example.test", null, null, null, "External", "Instagram");
    private static string Purchase(string operation, Guid? target, byte[]? version, DraftContentV2 draft) => PurchasingIdentityInput.Canonical(operation, target, version is null ? null : Convert.ToBase64String(version), draft);
    [Fact]
    public async Task CounterAndSupplierTransactionsSerializeAndTenantNumbersRemainIndependent()
    {
        // GIVEN two tenant contexts and two connections competing within the first business.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var other = Guid.NewGuid(); var actor = Guid.NewGuid(); var otherActor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, other); await SeedActor(database, tenant, actor); await SeedActor(database, other, otherActor);
        var web = await database.CreateWebUserAsync(); await using var a = await Open(database, web, tenant); await using var b = await Open(database, web, tenant); await using var foreign = await Open(database, web, other);
        // WHEN independent creations race THEN the locked tenant counter allocates distinct permanent numbers.
        var creates = await Task.WhenAll(Save(a, actor, Guid.NewGuid(), Purchase("Create", null, null, Snapshot()), "Create"), Save(b, actor, Guid.NewGuid(), Purchase("Create", null, null, Snapshot()), "Create"));
        await using var count = new SqlCommand("SELECT COUNT(DISTINCT PoNumber) FROM Purchasing.DraftOrders", a); Assert.Equal(2, await count.ExecuteScalarAsync());
        var outside = await Save(foreign, otherActor, Guid.NewGuid(), Purchase("Create", null, null, Snapshot()), "Create");
        await using var firstNumber = new SqlCommand("SELECT PoNumber FROM Purchasing.DraftOrders WHERE Id=@id", foreign); firstNumber.Parameters.AddWithValue("@id", outside.Id); Assert.Equal(1L, await firstNumber.ExecuteScalarAsync());
        // AND rollback removes both allocation and durable content, while exhaustion creates no receipt.
        await using (var begin = new SqlCommand("BEGIN TRANSACTION", a)) await begin.ExecuteNonQueryAsync();
        await Save(a, actor, Guid.NewGuid(), Purchase("Create", null, null, Snapshot()), "Create");
        await using (var rollback = new SqlCommand("ROLLBACK", a)) await rollback.ExecuteNonQueryAsync();
        await using var counter = new SqlCommand("SELECT LastNumber FROM Purchasing.PurchaseOrderCounters", a); Assert.Equal(2L, await counter.ExecuteScalarAsync());
        await using var admin = new SqlConnection(database.AdminConnectionString); await admin.OpenAsync();
        await using var exhaust = new SqlCommand("UPDATE Purchasing.PurchaseOrderCounters SET LastNumber=9223372036854775807 WHERE TenantId=@tenant", admin); exhaust.Parameters.AddWithValue("@tenant", tenant); await exhaust.ExecuteNonQueryAsync();
        Assert.Equal(50413, (await Assert.ThrowsAsync<SqlException>(() => Save(a, actor, Guid.NewGuid(), Purchase("Create", null, null, Snapshot()), "Create"))).Number);
        Assert.Equal(2, await count.ExecuteScalarAsync());
    }
    [Fact]
    public async Task ArchiveAndSelectionRaceHasAnAtomicOutcomeAndForeignLinksAreHidden()
    {
        // GIVEN a shared active supplier, two concurrent tenant connections and a foreign business.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var other = Guid.NewGuid(); var actor = Guid.NewGuid(); var otherActor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, other); await SeedActor(database, tenant, actor); await SeedActor(database, other, otherActor);
        var web = await database.CreateWebUserAsync(); await using var a = await Open(database, web, tenant); await using var b = await Open(database, web, tenant); await using var foreign = await Open(database, web, other);
        var supplier = await SaveSupplier(a, actor, Guid.NewGuid(), SupplierCanonical("Create", null, null, new("Supplier", null, null, null, null, null)));
        // WHEN archive races a new selection THEN the selection either saves before archive or reports a recoverable conflict.
        async Task<int> Select()
        {
            try { await Save(a, actor, Guid.NewGuid(), Purchase("Create", null, null, Snapshot(supplier.Id)), "Create"); return 0; }
            catch (SqlException error) { return error.Number; }
        }
        var selected = Select(); var archived = SaveSupplier(b, actor, Guid.NewGuid(), SupplierCanonical("Archive", supplier.Id, supplier.Version, null, true));
        await Task.WhenAll(selected, archived); Assert.Contains(await selected, new[] { 0, 50412 });
        Assert.Equal(50412, (await Assert.ThrowsAsync<SqlException>(() => Save(a, actor, Guid.NewGuid(), Purchase("Create", null, null, Snapshot(supplier.Id)), "Create"))).Number);
        // AND cross-tenant IDs cannot select, read or mutate a supplier; same-name suppliers remain distinct.
        Assert.Equal(50414, (await Assert.ThrowsAsync<SqlException>(() => Save(foreign, otherActor, Guid.NewGuid(), Purchase("Create", null, null, Snapshot(supplier.Id)), "Create"))).Number);
        Assert.Equal(50504, (await Assert.ThrowsAsync<SqlException>(() => SaveSupplier(foreign, otherActor, Guid.NewGuid(), SupplierCanonical("Archive", supplier.Id, supplier.Version, null, false)))).Number);
        await using var hidden = new SqlCommand("SELECT (SELECT COUNT(*) FROM Purchasing.Suppliers)+(SELECT COUNT(*) FROM Purchasing.SupplierRequestReceipts)", foreign); Assert.Equal(0, await hidden.ExecuteScalarAsync());
        var duplicate = await SaveSupplier(a, actor, Guid.NewGuid(), SupplierCanonical("Create", null, null, new("Supplier", null, null, null, null, null))); Assert.NotEqual(supplier.Id, duplicate.Id);
    }
    [Theory]
    [InlineData("a\u00a0b@example.test")]
    [InlineData("a\u2000b@example.test")]
    [InlineData("a\u2028b@example.test")]
    [InlineData("two@@example.test")]
    [InlineData("a@example.test,b@example.test")]
    public async Task RestrictedCommandsValidateContactEmailIndependentlyOfHttp(string email)
    {
        // GIVEN a restricted web connection which bypasses API validation.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync(); var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        // WHEN malformed single-address email reaches either command THEN neither directory nor purchase content is saved.
        Assert.Equal(50500, (await Assert.ThrowsAsync<SqlException>(() => SaveSupplier(connection, actor, Guid.NewGuid(), SupplierCanonical("Create", null, null, new("Supplier", null, email, null, null, null))))).Number);
        Assert.Equal(50400, (await Assert.ThrowsAsync<SqlException>(() => Save(connection, actor, Guid.NewGuid(), Purchase("Create", null, null, Snapshot() with { SupplierEmail = email }), "Create"))).Number);
        await using var count = new SqlCommand("SELECT (SELECT COUNT(*) FROM Purchasing.Suppliers)+(SELECT COUNT(*) FROM Purchasing.SupplierRequestReceipts)+(SELECT COUNT(*) FROM Purchasing.DraftOrders)+(SELECT COUNT(*) FROM Purchasing.DraftOrderRequestReceipts)", connection); Assert.Equal(0, await count.ExecuteScalarAsync());
    }
    [Fact]
    public async Task SupplierReceiptsAndCountersDenyDirectRuntimeWritesAndUnauthorizedReads()
    {
        // GIVEN a restricted runtime connection without tenant authority.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync(); await using var connection = new SqlConnection(await database.CreateWebUserAsync()); await connection.OpenAsync();
        foreach (var table in new[] { "Suppliers", "SupplierRequestReceipts", "PurchaseOrderCounters" })
        {
            // WHEN SQL is queried directly THEN private rows are hidden and all direct mutations are denied.
            await using var read = new SqlCommand($"SELECT COUNT(*) FROM Purchasing.{table}", connection); Assert.Equal(0, await read.ExecuteScalarAsync());
            foreach (var statement in new[] { $"UPDATE Purchasing.{table} SET TenantId=NEWID()", $"DELETE FROM Purchasing.{table}", $"INSERT Purchasing.{table}(TenantId) VALUES(NEWID())" })
            {
                await using var command = new SqlCommand(statement, connection); Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
            }
        }
        Assert.Equal(50503, (await Assert.ThrowsAsync<SqlException>(() => SaveSupplier(connection, Guid.NewGuid(), Guid.NewGuid(), SupplierCanonical("Create", null, null, new("Supplier", null, null, null, null, null))))).Number);
    }
    [Fact]
    public async Task RestrictedCommandAcceptsMaximumUnicodeContactWithoutTruncatingIt()
    {
        // GIVEN a restricted web principal with valid contact values at all text limits.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync(); var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var content = new SupplierContent(new string('名', 200), new string('人', 200), new string('文', 240) + "@example.test", new string('電', 100), "https://example.test/", new string('住', 2000));
        // WHEN its escaped canonical JSON reaches SQL THEN all original UTF-16 text is retained without truncation.
        var saved = await SaveSupplier(connection, actor, Guid.NewGuid(), SupplierCanonical("Create", null, null, content));
        await using var read = new SqlCommand("SELECT PostalAddress FROM Purchasing.Suppliers WHERE Id=@id", connection); read.Parameters.AddWithValue("@id", saved.Id); Assert.Equal(content.PostalAddress, await read.ExecuteScalarAsync());
    }
}
