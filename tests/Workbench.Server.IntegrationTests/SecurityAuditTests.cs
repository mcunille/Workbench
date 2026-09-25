// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.Tenancy;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SecurityAuditTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task WebPrincipalCanAppendButCannotUpdateOrDeleteAuditHistory()
    {
        // GIVEN an isolated current schema and a web principal with append-only audit authority.
        await using var database = await sqlServer.CreateMigratedDatabaseAsync();
        var webConnection = await database.CreateWebUserAsync();
        var tenantId = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenantId, Guid.NewGuid());
        await using var connection = new SqlConnection(webConnection);
        await connection.OpenAsync();
        var proof = new TenantContextProof(await database.GetTenantContextProofKeyAsync());
        await proof.ApplyAsync(connection, tenantId, CancellationToken.None);

        // WHEN the web principal appends an audit event THEN the insert succeeds.
        await ExecuteAsync(
            connection,
            $"""
            INSERT INTO [Security].[TenantSecurityAuditEvents]
                ([Id], [TenantId], [Action], [Outcome], [OccurredAtUtc])
            VALUES
                (NEWID(), '{tenantId}', N'test.appended', N'Succeeded', SYSUTCDATETIME())
            """);

        // AND it cannot update or delete the persisted history.
        await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            connection,
            "UPDATE [Security].[TenantSecurityAuditEvents] SET [Action] = N'tampered'"));
        await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            connection,
            "DELETE FROM [Security].[TenantSecurityAuditEvents]"));
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
