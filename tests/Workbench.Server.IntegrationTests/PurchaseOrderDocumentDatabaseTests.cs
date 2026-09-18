// Copyright (c) 2026 The White Stag Collection.
using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;
namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class PurchaseOrderDocumentDatabaseTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task RestrictedRuntimeCanReadButCannotMutateDocumentEvidenceDirectly()
    {
        // GIVEN the fully migrated database and its restricted runtime principal.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        await using var connection = new SqlConnection(await database.CreateWebUserAsync());
        await connection.OpenAsync();
        // WHEN inspecting each document table THEN read access exists and direct writes are denied.
        foreach (var table in new[] { "PurchaseOrderDocuments", "PurchaseOrderDocumentOperations" })
        {
            await using var read = new SqlCommand($"SELECT COUNT(*) FROM Purchasing.{table}", connection);
            Assert.Equal(0, await read.ExecuteScalarAsync());
            foreach (var statement in new[] { $"UPDATE Purchasing.{table} SET TenantId=NEWID()", $"DELETE FROM Purchasing.{table}", $"INSERT Purchasing.{table}(TenantId) VALUES(NEWID())" })
            {
                await using var denied = new SqlCommand(statement, connection);
                Assert.Equal(229, (await Assert.ThrowsAsync<SqlException>(() => denied.ExecuteNonQueryAsync())).Number);
            }
        }
    }
}
