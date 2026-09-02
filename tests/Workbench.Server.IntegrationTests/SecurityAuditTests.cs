// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Persistence;
using Workbench.Server.Security;
using Workbench.Server.Tenancy;
using Workbench.Server.IntegrationTests.Infrastructure;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SecurityAuditTests(SqlServerFixture sqlServer)
{
    [Fact]
    public void AuditWriterRejectsSensitiveMetadataNames()
    {
        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlServer("Server=unused;Database=unused")
            .Options;
        using var database = new WorkbenchDbContext(options, new TenantContext(Guid.NewGuid()));
        var writer = new SecurityAuditWriter(database, TimeProvider.System);

        Assert.Throws<ArgumentException>(() => writer.AppendTenant(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "test",
            "User",
            Guid.NewGuid(),
            "Succeeded",
            "correlation",
            new Dictionary<string, string> { ["recoveryToken"] = "must-not-appear" }));
    }

    [Fact]
    public async Task WebPrincipalCanAppendButCannotUpdateOrDeleteAuditHistory()
    {
        await using var database = await sqlServer.CreateDatabaseAsync();
        await Workbench.Server.Persistence.DatabaseMigrator.MigrateAsync(
            database.AdminConnectionString,
            CancellationToken.None);
        var webConnection = await database.CreateWebUserAsync();
        var tenantId = Guid.NewGuid();
        await database.SeedTenantAuditRowsAsync(tenantId, Guid.NewGuid());
        await using var connection = new SqlConnection(webConnection);
        await connection.OpenAsync();
        await using (var context = new SqlCommand(
            "EXEC sys.sp_set_session_context @key=N'TenantId', @value=@tenantId, @read_only=1",
            connection))
        {
            context.Parameters.AddWithValue("@tenantId", tenantId);
            await context.ExecuteNonQueryAsync();
        }

        await ExecuteAsync(
            connection,
            $"""
            INSERT INTO [Security].[TenantSecurityAuditEvents]
                ([Id], [TenantId], [Action], [Outcome], [OccurredAtUtc])
            VALUES
                (NEWID(), '{tenantId}', N'test.appended', N'Succeeded', SYSUTCDATETIME())
            """);

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
