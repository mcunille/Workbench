// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Workbench.Server.Tenancy;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ItemEditingDatabaseTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task CheckedCommandEnforcesCallerIsolationValidationAndSharedPhotoVersion()
    {
        // GIVEN real restricted connections for two tenants and one existing item.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var id = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenant, otherTenant);
        await using var owner = new SqlConnection(database.AdminConnectionString);
        await owner.OpenAsync();
        await using var seed = new SqlCommand("""
            INSERT [Inventory].[Items] ([Id],[TenantId],[TrackingKind],[Name],[CreatedAtUtc],[CreationRequestId])
            VALUES (@id,@tenant,'Individual',N'Original',SYSUTCDATETIME(),NEWID());
            SELECT [RowVersion] FROM [Inventory].[Items] WHERE [Id]=@id;
            """, owner);
        seed.Parameters.AddWithValue("@id", id);
        seed.Parameters.AddWithValue("@tenant", tenant);
        var version = (byte[])(await seed.ExecuteScalarAsync())!;
        var connectionString = await database.CreateWebUserAsync();
        var proof = new TenantContextProof(await database.GetTenantContextProofKeyAsync());
        await using var text = new SqlConnection(connectionString);
        await using var photo = new SqlConnection(connectionString);
        await using var foreign = new SqlConnection(connectionString);
        foreach (var connection in new[] { text, photo, foreign })
        {
            await connection.OpenAsync();
            await proof.ApplyAsync(connection, connection == foreign ? otherTenant : tenant, CancellationToken.None);
        }
        // WHEN direct SQL tries unsupported writes or oversized stored procedure inputs.
        foreach (var sql in new[] { "UPDATE [Inventory].[Items] SET [Name]=N'Bypass'", "DELETE FROM [Inventory].[Items]" })
        {
            await using var denied = new SqlCommand(sql, text);
            Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => denied.ExecuteNonQueryAsync())).Number);
        }
        foreach (var fields in new[] { (new string('n', 201), "", ""), ("Name", new string('n', 4001), ""), ("Name", "", new string('l', 201)), (" \t\u2003", "", "") })
        {
            var invalid = await Assert.ThrowsAsync<SqlException>(() => EditAsync(text, id, version, fields.Item1, fields.Item2, fields.Item3));
            Assert.Equal(50042, invalid.Number);
        }
        // THEN caller RLS hides foreign IDs, and valid exact boundaries are accepted without truncation.
        Assert.Equal(0, await EditAsync(foreign, id, version, "Foreign"));
        Assert.Equal(1, await EditAsync(text, id, version, new string('n', 200), new string('a', 4000), new string('l', 200)));
        await using var readVersion = new SqlCommand("SELECT [RowVersion] FROM [Inventory].[Items] WHERE [Id]=@id", owner);
        readVersion.Parameters.AddWithValue("@id", id);
        version = (byte[])(await readVersion.ExecuteScalarAsync())!;
        // WHEN text and photo pointer commands race over the same item version on separate SQL connections.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> TextAsync() { await start.Task; return await EditAsync(text, id, version, "Text wins") == 1; }
        async Task<bool> PhotoAsync()
        {
            await start.Task;
            await using var transaction = (SqlTransaction)await photo.BeginTransactionAsync();
            await using var command = new SqlCommand("EXEC [Inventory].[SetItemPhoto] @Id=@id,@ExpectedVersion=@version,@PhotoId=NULL", photo, transaction);
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@version", version);
            try { await command.ExecuteScalarAsync(); await transaction.CommitAsync(); return true; }
            catch (SqlException exception) when (exception.Number == 50040) { return false; }
        }
        var textTask = TextAsync();
        var photoTask = PhotoAsync();
        start.SetResult();
        var results = await Task.WhenAll(textTask, photoTask);
        // THEN exactly one version-checked mutation commits, and the old token cannot overwrite it.
        Assert.Single(results, won => won);
        Assert.Equal(2, await EditAsync(text, id, version, "Late retry"));
    }

    private static async Task<int> EditAsync(SqlConnection connection, Guid id, byte[] version, string name, string? notes = null, string? location = null)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using var command = new SqlCommand("EXEC [Inventory].[UpdateItemDetails] @Id=@id,@ExpectedVersion=@version,@Name=@name,@Notes=@notes,@Location=@location", connection, transaction);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@version", version);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@notes", (object?)notes ?? DBNull.Value);
        command.Parameters.AddWithValue("@location", (object?)location ?? DBNull.Value);
        var result = (int)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return result;
    }
}
