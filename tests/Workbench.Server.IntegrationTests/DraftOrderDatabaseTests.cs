// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;
using System.Text.Json;
using Workbench.Server.Tenancy;
namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed partial class DraftOrderDatabaseTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task ForwardValidationCorrectionPreservesSavedDraftAndRetryReceipt()
    {
        // GIVEN the retained purchasing schema with a successful draft save and compact receipt.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync("AddDraftSupplierOrders");
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid(); var request = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var canonical = Canonical("Create", null, null, "Retained draft");
        var saved = await Save(connection, actor, request, canonical, "Create");
        async Task<string> Snapshot()
        {
            await using var read = new SqlCommand("""
                SELECT (SELECT Id,TenantId,Title,SupplierName,Currency,Notes,ContentSchemaVersion,ContentJson,CreatedAtUtc,UpdatedAtUtc,CreatedByUserId,UpdatedByUserId,RowVersion FROM Purchasing.DraftOrders ORDER BY Id FOR JSON PATH) Drafts,
                    (SELECT * FROM Purchasing.DraftOrderRequestReceipts ORDER BY RequestId FOR JSON PATH) Receipts
                FOR JSON PATH,WITHOUT_ARRAY_WRAPPER
                """, connection);
            return (string)(await read.ExecuteScalarAsync())!;
        }
        var before = await Snapshot();
        // WHEN applying the forward-only validator correction THEN saved values, actors, times and fingerprint evidence remain byte-for-byte intact.
        await Workbench.Server.Persistence.DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        Assert.Equal(before, await Snapshot());
        // AND the original request still resolves to its original successful receipt.
        var replay = await Save(connection, actor, request, canonical, "Create");
        Assert.True(replay.Replayed); Assert.Equal(saved.Id, replay.Id); Assert.Equal(saved.Version, replay.Version); Assert.Equal(saved.Completed, replay.Completed);
    }

    [Theory]
    [InlineData("https://%zz/")]
    [InlineData("https://foo^bar/")]
    [InlineData("https://foo{bar}/")]
    [InlineData("https://.example/")]
    [InlineData("https://foo..example/")]
    [InlineData("https://supplier.example/cart\u0080")]
    public async Task RestrictedCommandRejectsMalformedHostsAndControlCharactersInBothLinkLocations(string link)
    {
        // GIVEN a restricted connection authorized to save a draft.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        foreach (var entryLink in new[] { false, true })
        {
            var canonical = JsonSerializer.Serialize(new
            {
                operation = "Create",
                targetId = (string?)null,
                expectedVersion = (string?)null,
                draft = new
                {
                    title = (string?)null,
                    supplierName = (string?)null,
                    currency = (string?)null,
                    notes = (string?)null,
                    sourceLinks = entryLink ? Array.Empty<string>() : [link],
                    entries = entryLink ? new[] { new { id = Guid.NewGuid(), description = (string?)null, notes = (string?)null, sourceLink = link, indicativePrice = (string?)null } } : [],
                },
            });
            // WHEN bypassing HTTP with an invalid link in either location THEN SQL rejects the save without durable side effects.
            Assert.Equal(50400, (await Assert.ThrowsAsync<SqlException>(() => Save(connection, actor, Guid.NewGuid(), canonical, "Create"))).Number);
        }
        await using var count = new SqlCommand("SELECT (SELECT COUNT(*) FROM Purchasing.DraftOrders)+(SELECT COUNT(*) FROM Purchasing.DraftOrderRequestReceipts)", connection);
        Assert.Equal(0, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RestrictedCommandRejectsMalformedIpv6HostsAndPreservesValidLinks()
    {
        // GIVEN a restricted connection with valid tenant authority.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        string Linked(string link) => JsonSerializer.Serialize(new
        {
            operation = "Create",
            targetId = (string?)null,
            expectedVersion = (string?)null,
            draft = new { title = (string?)null, supplierName = (string?)null, currency = (string?)null, notes = (string?)null, sourceLinks = new[] { link }, entries = Array.Empty<object>() },
        });
        // WHEN bypassing HTTP with malformed IPv6 source links THEN the restricted SQL command rejects them.
        foreach (var link in new[] { "https://[::::]/", "https://[1:2:3]/", "https://[1:2:3:4:5:6:7:8:9]/", "https://[12345::]/", "https://[::ffff:999.0.0.1]/" })
            Assert.Equal(50400, (await Assert.ThrowsAsync<SqlException>(() => Save(connection, actor, Guid.NewGuid(), Linked(link), "Create"))).Number);
        // AND ordinary DNS, compressed/uncompressed IPv6 and embedded IPv4 remain valid without URL rewriting.
        foreach (var link in new[] { "https://supplier.example/cart", "https://foo_bar.example./cart?filter=foo%5Ebar&group={items}", "https://münchen.example/cart", "https://192.0.2.1/cart", "https://[::1]:8443/cart", "http://[::]/", "https://[1:2:3:4:5:6:7:8]/", "http://[::ffff:192.0.2.1]/" })
        {
            var saved = await Save(connection, actor, Guid.NewGuid(), Linked(link), "Create");
            await using var read = new SqlCommand("SELECT JSON_VALUE(ContentJson,'$.sourceLinks[0]') FROM Purchasing.DraftOrders WHERE Id=@id", connection);
            read.Parameters.AddWithValue("@id", saved.Id); Assert.Equal(link, await read.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task WorkersHaveNoPurchasingAuthorityAndMissingTenantProofCannotSave()
    {
        // GIVEN fully migrated restricted worker and web principals without a tenant proof.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        await using var worker = new SqlConnection(await database.CreateRoleUserAsync("workbench_worker"));
        await worker.OpenAsync();
        // WHEN worker code attempts to read purchasing data or call its public commands THEN SQL denies access.
        foreach (var statement in new[] { "SELECT COUNT(*) FROM Purchasing.DraftOrders", "SELECT COUNT(*) FROM Purchasing.DraftOrderRequestReceipts", "EXEC Purchasing.CreateDraftOrder", "EXEC Purchasing.UpdateDraftOrder", "EXEC Purchasing.DeleteDraftOrder", "EXEC Purchasing.SaveDraftOrder" })
        {
            await using var denied = new SqlCommand(statement, worker);
            Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => denied.ExecuteNonQueryAsync())).Number);
        }
        await using var web = new SqlConnection(await database.CreateWebUserAsync()); await web.OpenAsync();
        // AND knowing identifiers alone cannot authorize a web command.
        Assert.Equal(50403, (await Assert.ThrowsAsync<SqlException>(() => Save(web, Guid.NewGuid(), Guid.NewGuid(), Canonical("Create", null, null, null), "Create"))).Number);
        await using var privateCommand = new SqlCommand("EXEC Purchasing.SaveDraftOrder", web);
        Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => privateCommand.ExecuteNonQueryAsync())).Number);
    }

    [Fact]
    public async Task DestructiveDownPreservesDraftAndCompactReceipt()
    {
        // GIVEN a current schema containing durable draft and request evidence.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var saved = await Save(connection, actor, Guid.NewGuid(), Canonical("Create", null, null, "Retained"), "Create");
        // WHEN the restricted migrator attempts Down THEN the guard preserves both rows.
        var migrator = await database.CreateRoleUserAsync("workbench_migrator");
        Assert.Equal(50020, (await Assert.ThrowsAsync<SqlException>(() => Workbench.Server.Persistence.DatabaseMigrator.MigrateToAsync(migrator, "AddAcquisitionDocuments", default))).Number);
        await using var read = new SqlCommand("SELECT (SELECT COUNT(*) FROM Purchasing.DraftOrders WHERE Id=@id)+(SELECT COUNT(*) FROM Purchasing.DraftOrderRequestReceipts WHERE DraftOrderId=@id)", connection);
        read.Parameters.AddWithValue("@id", saved.Id); Assert.Equal(2, await read.ExecuteScalarAsync());
    }

    [Fact]
    public async Task TenantIsolationAndCurrentActorAuthorityApplyToReplaysAndForeignTargets()
    {
        // GIVEN two tenants with enabled actors and a successful request in the first tenant.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var actorA = Guid.NewGuid(); var actorB = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(a, b);
        await SeedActor(database, a, actorA); await SeedActor(database, b, actorB);
        var web = await database.CreateWebUserAsync();
        await using var connectionA = await Open(database, web, a);
        await using var connectionB = await Open(database, web, b);
        var request = Guid.NewGuid(); var canonical = Canonical("Create", null, null, "Private A");
        var savedA = await Save(connectionA, actorA, request, canonical, "Create");
        // WHEN another tenant uses the same request UUID THEN it creates its own independent draft.
        var savedB = await Save(connectionB, actorB, request, canonical, "Create");
        Assert.False(savedB.Replayed); Assert.NotEqual(savedA.Id, savedB.Id);
        // AND unfiltered SQL still cannot read or update the first tenant's draft.
        await using var read = new SqlCommand("SELECT COUNT(*) FROM Purchasing.DraftOrders WHERE Id=@id", connectionB);
        read.Parameters.AddWithValue("@id", savedA.Id); Assert.Equal(0, await read.ExecuteScalarAsync());
        Assert.Equal(50404, (await Assert.ThrowsAsync<SqlException>(() => Save(connectionB, actorB, request, Canonical("Update", savedA.Id, savedA.Version, "Foreign"), "Update"))).Number);
        Assert.Equal(50403, (await Assert.ThrowsAsync<SqlException>(() => Save(connectionB, actorA, request, canonical, "Create"))).Number);
        // WHEN the original actor is disabled THEN a previously successful request cannot bypass authority.
        await using var admin = new SqlConnection(database.AdminConnectionString); await admin.OpenAsync();
        await using var disable = new SqlCommand("UPDATE [Identity].[Users] SET State=2 WHERE Id=@id", admin);
        disable.Parameters.AddWithValue("@id", actorA); await disable.ExecuteNonQueryAsync();
        Assert.Equal(50403, (await Assert.ThrowsAsync<SqlException>(() => Save(connectionA, actorA, request, canonical, "Create"))).Number);
    }

    [Fact]
    public async Task CompetingRequestsSerializeAndReceiptRollsBackWithDraft()
    {
        // GIVEN two connections authorized for the same tenant.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        var web = await database.CreateWebUserAsync();
        await using var a = await Open(database, web, tenant); await using var b = await Open(database, web, tenant);
        var request = Guid.NewGuid(); var canonical = Canonical("Create", null, null, null);
        // WHEN duplicate creations compete THEN exactly one mutation and one replay return the same receipt.
        var saves = await Task.WhenAll(Save(a, actor, request, canonical, "Create"), Save(b, actor, request, canonical, "Create"));
        Assert.Single(saves, row => row.Replayed); Assert.Equal(saves[0].Id, saves[1].Id); Assert.Equal(saves[0].Version, saves[1].Version);
        // WHEN two updates compete with the same expected version THEN one succeeds and the other conflicts.
        var update = Canonical("Update", saves[0].Id, saves[0].Version, "Changed");
        async Task<int> Compete(SqlConnection connection)
        {
            try { await Save(connection, actor, Guid.NewGuid(), update, "Update"); return 0; }
            catch (SqlException error) { return error.Number; }
        }
        Assert.Equal(new[] { 0, 50409 }, (await Task.WhenAll(Compete(a), Compete(b))).Order().ToArray());
        // WHEN an enclosing transaction rolls back THEN both the draft and receipt disappear together.
        await using (var begin = new SqlCommand("BEGIN TRANSACTION", a)) await begin.ExecuteNonQueryAsync();
        var rolledBack = await Save(a, actor, Guid.NewGuid(), canonical, "Create");
        await using (var rollback = new SqlCommand("ROLLBACK", a)) await rollback.ExecuteNonQueryAsync();
        await using var count = new SqlCommand("SELECT (SELECT COUNT(*) FROM Purchasing.DraftOrders WHERE Id=@id)+(SELECT COUNT(*) FROM Purchasing.DraftOrderRequestReceipts WHERE DraftOrderId=@id)", a);
        count.Parameters.AddWithValue("@id", rolledBack.Id); Assert.Equal(0, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CurrencyTransitionRequiresAPriceClearingSaveAndMalformedPricesAreRejected()
    {
        // GIVEN a draft with an explicit zero price denominated in USD.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid()); await SeedActor(database, tenant, actor);
        await using var connection = await Open(database, await database.CreateWebUserAsync(), tenant);
        var entry = Guid.NewGuid();
        string Priced(string operation, Guid? target, byte[]? version, string? currency, string? price) => JsonSerializer.Serialize(new
        {
            operation,
            targetId = target?.ToString("D"),
            expectedVersion = version is null ? null : Convert.ToBase64String(version),
            draft = new
            {
                title = (string?)null,
                supplierName = (string?)null,
                currency,
                notes = (string?)null,
                sourceLinks = Array.Empty<string>(),
                entries = new[] { new { id = entry, description = (string?)null, notes = (string?)null, sourceLink = (string?)null, indicativePrice = price } }
            },
        });
        var saved = await Save(connection, actor, Guid.NewGuid(), Priced("Create", null, null, "USD", "0.0000"), "Create");
        // WHEN currency changes while a price is present THEN the command refuses to relabel that amount.
        Assert.Equal(50401, (await Assert.ThrowsAsync<SqlException>(() => Save(connection, actor, Guid.NewGuid(), Priced("Update", saved.Id, saved.Version, "EUR", "0.0000"), "Update"))).Number);
        var cleared = await Save(connection, actor, Guid.NewGuid(), Priced("Update", saved.Id, saved.Version, "EUR", null), "Update");
        var repriced = await Save(connection, actor, Guid.NewGuid(), Priced("Update", saved.Id, cleared.Version, "EUR", "999999999999999.9999"), "Update");
        Assert.NotEqual(cleared.Version, repriced.Version);
        // AND SQL independently rejects precision loss, exponents, missing currency and noncanonical decimal strings.
        foreach (var price in new[] { "0.00001", "1e2", "-1.0000", "01.0000", ".0000", "1000000000000000.0000" })
            Assert.Equal(50400, (await Assert.ThrowsAsync<SqlException>(() => Save(connection, actor, Guid.NewGuid(), Priced("Create", null, null, "USD", price), "Create"))).Number);
        Assert.Equal(50400, (await Assert.ThrowsAsync<SqlException>(() => Save(connection, actor, Guid.NewGuid(), Priced("Create", null, null, null, "0.0000"), "Create"))).Number);
    }

    private static async Task SeedActor(SqlTestDatabase database, Guid tenant, Guid actor)
    {
        await using var admin = new SqlConnection(database.AdminConnectionString); await admin.OpenAsync();
        await using var seed = new SqlCommand("""
            INSERT [Identity].[Users](Id,TenantId,State,CreatedAtUtc,EmailConfirmed,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnabled,AccessFailedCount)
            VALUES(@actor,@tenant,1,SYSUTCDATETIME(),0,0,0,0,0)
            """, admin);
        seed.Parameters.AddWithValue("@actor", actor); seed.Parameters.AddWithValue("@tenant", tenant); await seed.ExecuteNonQueryAsync();
    }

    private static async Task<SqlConnection> Open(SqlTestDatabase database, string web, Guid tenant)
    {
        var connection = new SqlConnection(web); await connection.OpenAsync();
        await new TenantContextProof(await database.GetTenantContextProofKeyAsync()).ApplyAsync(connection, tenant, default);
        return connection;
    }

    [Fact]
    public async Task CommandReplaysOriginalReceiptAfterLaterEditAndRejectsChangedInput()
    {
        // GIVEN a valid tenant actor and a saved empty draft.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid());
        await using (var admin = new SqlConnection(database.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var seed = new SqlCommand("""
                INSERT [Identity].[Users](Id,TenantId,State,CreatedAtUtc,EmailConfirmed,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnabled,AccessFailedCount)
                VALUES(@actor,@tenant,1,SYSUTCDATETIME(),0,0,0,0,0)
                """, admin);
            seed.Parameters.AddWithValue("@actor", actor); seed.Parameters.AddWithValue("@tenant", tenant);
            await seed.ExecuteNonQueryAsync();
        }
        await using var connection = new SqlConnection(await database.CreateWebUserAsync());
        await connection.OpenAsync();
        await new TenantContextProof(await database.GetTenantContextProofKeyAsync()).ApplyAsync(connection, tenant, default);
        var request = Guid.NewGuid();
        var original = Canonical("Create", null, null, null);
        var created = await Save(connection, actor, request, original, "Create");
        // WHEN another request edits the draft before the original request is retried.
        var updated = await Save(connection, actor, Guid.NewGuid(), Canonical("Update", created.Id, created.Version, "Later edit"), "Update");
        var replay = await Save(connection, actor, request, original, "Create");
        // THEN the original receipt is stable and replay does not overwrite current content.
        Assert.True(replay.Replayed); Assert.Equal(created.Id, replay.Id);
        Assert.Equal(created.Version, replay.Version); Assert.Equal(created.Completed, replay.Completed);
        Assert.NotEqual(created.Version, updated.Version);
        await using var current = new SqlCommand("SELECT Title FROM Purchasing.DraftOrders WHERE Id=@id", connection);
        current.Parameters.AddWithValue("@id", created.Id); Assert.Equal("Later edit", await current.ExecuteScalarAsync());
        // AND changed input under the same identifier is rejected.
        Assert.Equal(50410, (await Assert.ThrowsAsync<SqlException>(() => Save(connection, actor, request, Canonical("Create", null, null, "Different"), "Create"))).Number);
        // AND a stale new update is rejected without inserting receipt evidence.
        Assert.Equal(50409, (await Assert.ThrowsAsync<SqlException>(() => Save(connection, actor, Guid.NewGuid(), Canonical("Update", created.Id, created.Version, "Stale"), "Update"))).Number);
        await using var count = new SqlCommand("SELECT COUNT(*) FROM Purchasing.DraftOrderRequestReceipts", connection);
        Assert.Equal(2, await count.ExecuteScalarAsync());
        await using var inventory = new SqlCommand("SELECT (SELECT COUNT(*) FROM Inventory.Items)+(SELECT COUNT(*) FROM Inventory.Acquisitions)", connection);
        Assert.Equal(0, await inventory.ExecuteScalarAsync());
    }

    private static string Canonical(string operation, Guid? targetId, byte[]? version, string? title) => JsonSerializer.Serialize(new
    {
        operation,
        targetId = targetId?.ToString("D"),
        expectedVersion = version is null ? null : Convert.ToBase64String(version),
        draft = new { title, supplierName = (string?)null, currency = (string?)null, notes = (string?)null, sourceLinks = Array.Empty<string>(), entries = Array.Empty<object>() },
    });

    private static async Task<(Guid Id, byte[] Version, DateTimeOffset Completed, bool Replayed)> Save(SqlConnection connection, Guid actor, Guid request, string canonical, string operation)
    {
        await using var command = new SqlCommand($"Purchasing.{operation}DraftOrder", connection) { CommandType = System.Data.CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@RequestId", request); command.Parameters.AddWithValue("@ActorUserId", actor);
        command.Parameters.AddWithValue("@CanonicalInputJson", canonical);
        await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        return (reader.GetGuid(reader.GetOrdinal("DraftOrderId")), (byte[])reader["SavedVersion"], reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("CompletedAtUtc")), reader.GetBoolean(reader.GetOrdinal("Replayed")));
    }

    [Fact]
    public async Task RestrictedRuntimeCanReadButCannotDirectlyMutateDraftsOrReceipts()
    {
        // GIVEN the current schema and restricted runtime principal.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        await using var connection = new SqlConnection(await database.CreateWebUserAsync());
        await connection.OpenAsync();
        foreach (var table in new[] { "DraftOrders", "DraftOrderRequestReceipts" })
        {
            // WHEN reading without tenant authority THEN no private rows are visible.
            await using var read = new SqlCommand($"SELECT COUNT(*) FROM Purchasing.{table}", connection);
            Assert.Equal(0, await read.ExecuteScalarAsync());
            // WHEN bypassing commands THEN every direct mutation is denied.
            foreach (var statement in new[] { $"UPDATE Purchasing.{table} SET TenantId=NEWID()", $"DELETE FROM Purchasing.{table}", $"INSERT Purchasing.{table}(TenantId) VALUES(NEWID())" })
            {
                await using var denied = new SqlCommand(statement, connection);
                Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => denied.ExecuteNonQueryAsync())).Number);
            }
        }
    }
}
