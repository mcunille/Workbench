// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class AcquisitionDatabaseTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task RestrictedCommandsEnforceValidationTenantIsolationAndImmutableReplay()
    {
        // GIVEN two tenant contexts on real restricted SQL connections.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, otherTenant);
        await using var admin = new SqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        var id = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var version = await SeedItemAsync(admin, tenant, id);
        await SeedItemAsync(admin, otherTenant, otherId);
        var connectionString = await database.CreateWebUserAsync();
        var proof = new TenantContextProof(await database.GetTenantContextProofKeyAsync());
        await using var owner = new SqlConnection(connectionString);
        await using var foreign = new SqlConnection(connectionString);
        await owner.OpenAsync();
        await foreign.OpenAsync();
        await proof.ApplyAsync(owner, tenant, default);
        await proof.ApplyAsync(foreign, otherTenant, default);
        // WHEN direct writes or oversized/malformed procedure inputs are attempted THEN SQL rejects them.
        foreach (var table in new[] { "Acquisitions", "AcquisitionItems", "AcquisitionCreationRecords" })
        {
            foreach (var statement in new[] { $"UPDATE Inventory.{table} SET TenantId=NEWID()", $"DELETE FROM Inventory.{table}", $"INSERT Inventory.{table} (TenantId) VALUES (NEWID())" })
            {
                await using var denied = new SqlCommand(statement, owner);
                Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => denied.ExecuteNonQueryAsync())).Number);
            }
        }
        foreach (var fields in new[] {
            new Fields("gift"), new Fields("Gift "), new Fields(new string('m', 100)), new Fields(Source: new string('s', 201)),
            new Fields(Notes: new string('n', 4001)), new Fields(Month: 2), new Fields(Day: 1), new Fields(Year: 0),
            new Fields(Year: 10000), new Fields(Year: 2024, Month: 13), new Fields(Year: 2023, Month: 2, Day: 29),
            new Fields(Year: 1900, Month: 2, Day: 29), new Fields(Year: 9999), new Fields(Year: 2024, Day: 1) })
            Assert.Equal(50045, (await Assert.ThrowsAsync<SqlException>(() => CreateAsync(owner, id, Guid.NewGuid(), version, fields))).Number);
        foreach (var badVersion in new[] { Array.Empty<byte>(), new byte[7], new byte[9] })
            Assert.Equal(50045, (await Assert.ThrowsAsync<SqlException>(() => CreateAsync(owner, id, Guid.NewGuid(), badVersion, new()))).Number);
        await using (var noTransaction = CreateCommand(owner, null, id, Guid.NewGuid(), version, new()))
            Assert.Equal(50045, (await Assert.ThrowsAsync<SqlException>(() => noTransaction.ExecuteScalarAsync())).Number);
        Assert.Equal(0, await CreateAsync(foreign, id, Guid.NewGuid(), version, new()));
        // WHEN every valid method is saved with precise calendar boundaries THEN no input is truncated.
        foreach (var method in new[] { "Purchase", "Gift", "Inheritance", "Trade", "Other", "Unknown" })
        {
            var piece = Guid.NewGuid();
            var token = await SeedItemAsync(admin, tenant, piece);
            var request = Guid.NewGuid();
            var fields = new Fields(method, new string('s', 200), 2000, 2, 29, new string('n', 4000));
            Assert.Equal(1, await CreateAsync(owner, piece, request, token, fields));
            Assert.Equal(4, await CreateAsync(owner, piece, request, token, fields));
        }
        var creation = Guid.NewGuid();
        foreach (var fields in new[] { new Fields(Year: 1), new Fields(Year: 2024, Month: 2), new Fields(Year: 2024, Month: 2, Day: 29) })
        {
            var piece = Guid.NewGuid();
            var token = await SeedItemAsync(admin, tenant, piece);
            Assert.Equal(1, await CreateAsync(owner, piece, Guid.NewGuid(), token, fields));
        }
        Assert.Equal(1, await CreateAsync(owner, id, creation, version, new(Source: " \u2003Family\t", Notes: " \t\u2003")));
        Assert.Equal(4, await CreateAsync(owner, id, creation, version, new(Source: "Family")));
        foreach (var table in new[] { "Acquisitions", "AcquisitionItems", "AcquisitionCreationRecords" })
        {
            await using var hidden = new SqlCommand($"SELECT COUNT(*) FROM Inventory.{table}", foreign);
            Assert.Equal(0, await hidden.ExecuteScalarAsync());
        }
        // AND owner-level attempts to forge cross-tenant links fail the tenant-qualified FK.
        await using var crossTenant = new SqlCommand("INSERT Inventory.AcquisitionItems (TenantId,ItemId,AcquisitionId) SELECT @tenant,@item,Id FROM Inventory.Acquisitions WHERE CreationRequestId=@request", admin);
        crossTenant.Parameters.AddWithValue("@tenant", otherTenant);
        crossTenant.Parameters.AddWithValue("@item", otherId);
        crossTenant.Parameters.AddWithValue("@request", creation);
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => crossTenant.ExecuteNonQueryAsync())).Number);
        // AND malformed dates cannot bypass the table constraint through privileged SQL.
        foreach (var invalid in new[] { "[Year]=NULL,[Month]=2,[Day]=NULL", "[Year]=2024,[Month]=NULL,[Day]=1", "[Year]=2023,[Month]=2,[Day]=29" })
        {
            await using var malformed = new SqlCommand($"UPDATE Inventory.Acquisitions SET {invalid} WHERE CreationRequestId=@request", admin);
            malformed.Parameters.AddWithValue("@request", creation);
            Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => malformed.ExecuteNonQueryAsync())).Number);
        }
        await using var badMethod = new SqlCommand("UPDATE Inventory.Acquisitions SET Method=N'Gift ' WHERE CreationRequestId=@request", admin);
        badMethod.Parameters.AddWithValue("@request", creation);
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => badMethod.ExecuteNonQueryAsync())).Number);
    }

    [Theory]
    [InlineData("AddItemRestoration")]
    [InlineData("AddSharedAcquisitions")]
    public async Task UpgradeFromRestorationRetainsRecordsAndDownRefusesAcquisitionLoss(string priorMigration)
    {
        // GIVEN active and archived items, immutable creation evidence, and a retained photo on the PR base.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateToAsync(database.AdminConnectionString, priorMigration, default);
        var tenant = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, Guid.NewGuid());
        await using var admin = new SqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();
        var id = Guid.NewGuid();
        var archived = Guid.NewGuid();
        await SeedItemAsync(admin, tenant, id);
        await SeedItemAsync(admin, tenant, archived);
        await using (var seed = new SqlCommand("""
            INSERT Inventory.ItemCreationSnapshots (TenantId,ItemId,Name,Notes,StorageLocation)
                SELECT TenantId,Id,Name,Notes,StorageLocation FROM Inventory.Items WHERE Id=@id;
            UPDATE Inventory.Items SET Name=N'Edited',ArchivedAtUtc=SYSUTCDATETIME() WHERE Id=@archived;
            DECLARE @detail uniqueidentifier=NEWID(),@thumb uniqueidentifier=NEWID(),@operation uniqueidentifier=NEWID(),@photo uniqueidentifier=NEWID();
            INSERT Storage.Attachments (Id,TenantId,CreatedAtUtc) VALUES (@detail,@tenant,SYSUTCDATETIME()),(@thumb,@tenant,SYSUTCDATETIME());
            INSERT Inventory.ItemPhotoOperations (Id,TenantId,ItemId,RequestId,ExpectedVersion,PayloadSha256,Kind,State,ActorUserId,CreatedAtUtc,DetailAttachmentId,ThumbnailAttachmentId,ResultVersion)
                SELECT @operation,@tenant,@id,NEWID(),RowVersion,REPLICATE('A',64),0,1,NEWID(),SYSUTCDATETIME(),@detail,@thumb,RowVersion FROM Inventory.Items WHERE Id=@id;
            INSERT Inventory.ItemPhotos (Id,TenantId,ItemId,OperationId,DetailAttachmentId,ThumbnailAttachmentId,Width,Height,CreatedAtUtc)
                VALUES (@photo,@tenant,@id,@operation,@detail,@thumb,640,480,SYSUTCDATETIME());
            UPDATE Inventory.Items SET CurrentPhotoId=@photo WHERE Id=@id;
            """, admin))
        {
            seed.Parameters.AddWithValue("@id", id);
            seed.Parameters.AddWithValue("@archived", archived);
            seed.Parameters.AddWithValue("@tenant", tenant);
            await seed.ExecuteNonQueryAsync();
        }
        var before = await RetainedAsync(admin);
        // WHEN upgrading the actual previous schema THEN all existing records and tokens survive exactly.
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        Assert.Equal(before, await RetainedAsync(admin));
        await using var marker = new SqlCommand("SELECT OBJECT_DEFINITION(OBJECT_ID(N'Security.ReadDatabaseReadiness'))", admin);
        Assert.Contains("20260912033355_TightenDraftSourceLinkValidation", (string)(await marker.ExecuteScalarAsync())!);
        await using var tokenRead = new SqlCommand("SELECT RowVersion FROM Inventory.Items WHERE Id=@id", admin);
        tokenRead.Parameters.AddWithValue("@id", id);
        var version = (byte[])(await tokenRead.ExecuteScalarAsync())!;
        Assert.Equal(1, await CreateAsync(admin, id, Guid.NewGuid(), version, new()));
        // WHEN a tenant-filtered migrator requests destructive Down THEN saved acquisition data is retained.
        var migrator = await database.CreateRoleUserAsync("workbench_migrator");
        Assert.Equal(50020, (await Assert.ThrowsAsync<SqlException>(() => DatabaseMigrator.MigrateToAsync(migrator, "AddItemRestoration", default))).Number);
        await using var count = new SqlCommand("SELECT COUNT(*) FROM Inventory.Acquisitions", admin);
        Assert.Equal(1, await count.ExecuteScalarAsync());
    }

    private static async Task<string> RetainedAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand("""
            SELECT (SELECT * FROM Inventory.Items ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES) +
                (SELECT * FROM Inventory.ItemPhotos ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES) +
                (SELECT * FROM Inventory.ItemPhotoOperations ORDER BY Id FOR JSON PATH,INCLUDE_NULL_VALUES) +
                (SELECT * FROM Inventory.ItemCreationSnapshots ORDER BY ItemId FOR JSON PATH,INCLUDE_NULL_VALUES);
            """, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<byte[]> SeedItemAsync(SqlConnection connection, Guid tenant, Guid id)
    {
        await using var command = new SqlCommand("""
            INSERT Inventory.Items (Id,TenantId,TrackingKind,Name,CreatedAtUtc,CreationRequestId)
                VALUES (@id,@tenant,'Individual',N'Piece',SYSUTCDATETIME(),NEWID());
            SELECT RowVersion FROM Inventory.Items WHERE Id=@id;
            """, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@tenant", tenant);
        return (byte[])(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> CreateAsync(SqlConnection connection, Guid id, Guid request, byte[] version, Fields fields)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = CreateCommand(connection, transaction, id, request, version, fields);
        var status = (int)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return status;
    }

    private static SqlCommand CreateCommand(SqlConnection connection, SqlTransaction? transaction, Guid id, Guid request, byte[] version, Fields fields)
    {
        var command = new SqlCommand("Inventory.CreateAcquisition", connection, transaction) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@ItemId", id);
        command.Parameters.AddWithValue("@CreationRequestId", request);
        command.Parameters.Add(new SqlParameter("@ExpectedItemVersion", SqlDbType.VarBinary, -1) { Value = version });
        command.Parameters.Add(new SqlParameter("@Method", SqlDbType.NVarChar, -1) { Value = fields.Method });
        command.Parameters.Add(new SqlParameter("@Source", SqlDbType.NVarChar, -1) { Value = (object?)fields.Source ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@Notes", SqlDbType.NVarChar, -1) { Value = (object?)fields.Notes ?? DBNull.Value });
        command.Parameters.AddWithValue("@Year", (object?)fields.Year ?? DBNull.Value);
        command.Parameters.AddWithValue("@Month", (object?)fields.Month ?? DBNull.Value);
        command.Parameters.AddWithValue("@Day", (object?)fields.Day ?? DBNull.Value);
        return command;
    }

    private sealed record Fields(string Method = "Gift", string? Source = null, int? Year = null, int? Month = null, int? Day = null, string? Notes = null);
}
