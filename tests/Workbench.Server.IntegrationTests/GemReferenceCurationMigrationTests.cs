// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class GemReferenceCurationMigrationTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task MigrationInstallsRestrictedCurationCommands()
    {
        // GIVEN the current schema and actual workload principals.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        foreach (var role in new[] { "workbench_web", "workbench_worker", "workbench_operator" })
        {
            await using var connection = new SqlConnection(await database.CreateRoleUserAsync(role));
            await connection.OpenAsync();
            // WHEN inspecting command access THEN only web can execute named curation commands.
            foreach (var name in new[] { "ReadDrafts", "ReadDraft", "SaveDraft", "ReadPublication", "ReadPublicationAudit", "PublishDraftBatch" })
            {
                await using var command = new SqlCommand("SELECT HAS_PERMS_BY_NAME(@name,'OBJECT','EXECUTE')", connection);
                command.Parameters.AddWithValue("@name", "Gemology." + name);
                Assert.Equal(role == "workbench_web" ? 1 : 0, Convert.ToInt32(await command.ExecuteScalarAsync()));
            }
            // AND raw private storage is inaccessible, including empty-table writes.
            foreach (var table in new[] { "Drafts", "PublishRequests", "PublicationAudit" })
            foreach (var sql in new[] { $"SELECT * FROM Gemology.{table}", $"DELETE FROM Gemology.{table}", $"UPDATE Gemology.{table} SET Id=Id", $"INSERT Gemology.{table} DEFAULT VALUES" })
            {
                await using var command = new SqlCommand(sql, connection);
                Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
            }
        }
    }

    [Fact]
    public async Task CommandsRejectMissingServiceAdminAuthority()
    {
        // GIVEN a web connection with no service-admin session.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        await using var connection = new SqlConnection(await database.CreateWebUserAsync());
        await connection.OpenAsync();
        // WHEN calling draft reads directly THEN account/session IDs do not manufacture authority.
        await using var command = new SqlCommand("EXEC Gemology.ReadDrafts @AccountId=@account,@SessionId=@session", connection);
        command.Parameters.AddWithValue("@account", Guid.NewGuid());
        command.Parameters.AddWithValue("@session", Guid.NewGuid());
        Assert.Equal(50041, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("version")]
    [InlineData("substitution")]
    public async Task CommandsRevalidateCurrentAuthority(string state)
    {
        // GIVEN a real admin account/session and a later authority change.
        await using var application = await AuthTestApplication.CreateAsync(sqlServer);
        await application.ProvisionServiceAdminAsync();
        var session = Guid.NewGuid();
        await ServiceAdminIdentityDatabaseTests.ScalarAsync<object>(application.AdminConnectionString, """
            INSERT ServiceAdministration.Sessions(Id,AccountId,SecurityVersion,TokenHash,CreatedAtUtc,LastSeenAtUtc,IdleExpiresAtUtc,AbsoluteExpiresAtUtc)
            VALUES(@session,@account,1,CRYPT_GEN_RANDOM(32),DATEADD(hour,-1,SYSUTCDATETIME()),DATEADD(hour,-1,SYSUTCDATETIME()),DATEADD(hour,1,SYSUTCDATETIME()),DATEADD(hour,2,SYSUTCDATETIME()));
            """, ("session", session), ("account", AuthTestApplication.ServiceAdminId));
        var mutation = state switch
        {
            "disabled" => "UPDATE ServiceAdministration.Accounts SET IsEnabled=0",
            "revoked" => "UPDATE ServiceAdministration.Sessions SET RevokedAtUtc=SYSUTCDATETIME(),RevocationReason=N'Logout'",
            "expired" => "UPDATE ServiceAdministration.Sessions SET IdleExpiresAtUtc=DATEADD(minute,-1,SYSUTCDATETIME())",
            "version" => "UPDATE ServiceAdministration.Accounts SET SecurityVersion=2",
            _ => "SELECT 1"
        };
        await ServiceAdminIdentityDatabaseTests.ScalarAsync<object>(application.AdminConnectionString, mutation);
        // WHEN bypassing HTTP and invoking SQL THEN stale or substituted identity is rejected.
        await using var connection = new SqlConnection(application.WebConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("EXEC Gemology.ReadDrafts @AccountId=@account,@SessionId=@session", connection);
        command.Parameters.AddWithValue("@account", state == "substitution" ? Guid.NewGuid() : AuthTestApplication.ServiceAdminId);
        command.Parameters.AddWithValue("@session", session);
        Assert.Equal(50041, (await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync())).Number);
    }
}
