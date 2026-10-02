// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class GemReferenceDatabaseTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task SharedSchemaEnforcesIdentityMaterialAndClaimOwnership()
    {
        // GIVEN stored mineral and organic references under the owner, independent of input validation.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var mineral = GemReferenceSamples.Mineral();
        await GemReferenceTestData.InsertAsync(database.AdminConnectionString, mineral);
        var other = mineral with { Id = Guid.NewGuid(), CommonName = "Pearl", MaterialKind = "organic",
            Species = null, Variety = null, Sources = [] };
        await GemReferenceTestData.InsertAsync(database.AdminConnectionString, other);
        await using var connection = new SqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        // WHEN direct writes attempt invalid identities, taxonomy, or source ownership THEN SQL rejects them.
        foreach (var sql in new[] {
            "UPDATE Gemology.Entries SET CommonName=N'Ruby',MaterialKind=N'mineral',Species=N'Corundum',Variety=N'Ruby',IdentityKey=(SELECT IdentityKey FROM Gemology.Entries WHERE Id=@mineral) WHERE Id=@other",
            "UPDATE Gemology.Entries SET Species=NULL WHERE Id=@mineral",
            "UPDATE Gemology.Entries SET MaterialKind=N'unknown' WHERE Id=@other",
            "UPDATE Gemology.Entries SET CommonName=N'  ' WHERE Id=@other",
            "UPDATE Gemology.Entries SET IsRetired=1,RedirectEntryId=Id WHERE Id=@other",
            "INSERT Gemology.Aliases(EntryId,Position,Name,NormalizedName) VALUES(@mineral,0,N'Ruby',N'RUBY'),(@mineral,1,N'ruby',N'RUBY')",
            "INSERT Gemology.LocalityAssertions(EntryId,Place,Scope,ReviewedOn,SourceAssertionId,SourceField) SELECT @other,N'Hills',N'Only known commercial source',ReviewedOn,Id,N'notableLocality' FROM Gemology.SourceAssertions WHERE EntryId=@mineral AND Field=N'commonName'",
        })
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            await using var command = new SqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("@mineral", mineral.Id);
            command.Parameters.AddWithValue("@other", other.Id);
            var failure = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains(failure.Number, new[] { 547, 2601, 2627 });
            await transaction.RollbackAsync();
        }
        // AND a retired identity can remain beside its active replacement.
        await using var retire = new SqlCommand("UPDATE Gemology.Entries SET IsRetired=1,RetirementExplanation=N'Replaced' WHERE Id=@id", connection);
        retire.Parameters.AddWithValue("@id", mineral.Id);
        await retire.ExecuteNonQueryAsync();
        await GemReferenceTestData.InsertAsync(database.AdminConnectionString, mineral with { Id = Guid.NewGuid(), Sources = [] });
    }

    [Fact]
    public async Task RestrictedPrincipalsReadOnlyTheSharedCatalog()
    {
        // GIVEN a populated catalog and distinct actual workload users.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        await GemReferenceTestData.InsertAsync(database.AdminConnectionString, GemReferenceSamples.Mineral());
        foreach (var role in new[] { "workbench_web", "workbench_worker", "workbench_operator", "workbench_storage_maintenance" })
        {
            await using var connection = new SqlConnection(await database.CreateRoleUserAsync(role));
            await connection.OpenAsync();
            foreach (var table in new[] { "Entries", "Aliases", "SourceAssertions", "LocalityAssertions" })
            {
                // WHEN reading THEN only web has shared read authority.
                await using var read = new SqlCommand($"SELECT COUNT(*) FROM Gemology.{table}", connection);
                if (role == "workbench_web") Assert.IsType<int>(await read.ExecuteScalarAsync());
                else Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => read.ExecuteScalarAsync())).Number);
                // AND runtime direct mutations fail even against an empty table.
                foreach (var sql in new[] { $"DELETE FROM Gemology.{table}",
                    $"UPDATE Gemology.{table} SET EntryId=EntryId", $"INSERT Gemology.{table} DEFAULT VALUES" })
                {
                    var statement = table == "Entries" ? sql.Replace("EntryId=EntryId", "Id=Id", StringComparison.Ordinal) : sql;
                    await using var write = new SqlCommand(statement, connection);
                    Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => write.ExecuteNonQueryAsync())).Number);
                }
            }
        }
    }

    [Fact]
    public async Task SharedReferenceTablesAreCreatedInCurrentSchema()
    {
        // GIVEN the current migrated schema.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        await using var connection = new SqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        foreach (var table in new[] { "Entries", "Aliases", "SourceAssertions", "LocalityAssertions" })
        {
            // WHEN inspecting shared storage THEN every specified table exists.
            await using var command = new SqlCommand("SELECT OBJECT_ID(@name, 'U')", connection);
            command.Parameters.AddWithValue("@name", $"Gemology.{table}");
            Assert.IsType<int>(await command.ExecuteScalarAsync());
        }
    }
}
