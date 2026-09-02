// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Workbench.Server.Administration;
using Workbench.Server.Identity;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class BootstrapTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task BootstrapCreatesExactlyOneTenantAndAdministrator()
    {
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var commands = new OperatorCommands(
            database.AdminConnectionString,
            new PasswordHasher<WorkbenchUser>(),
            TimeProvider.System);

        await commands.BootstrapAsync(
            "First Tenant",
            "admin@example.com",
            "Correct Horse Battery Staple 1!",
            CancellationToken.None);
        await Assert.ThrowsAsync<BootstrapAlreadyCompletedException>(() => commands.BootstrapAsync(
            "Second Tenant",
            "second@example.com",
            "Correct Horse Battery Staple 2@",
            CancellationToken.None));

        await using var connection = new SqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT
                (SELECT COUNT(*) FROM [Tenancy].[Tenants]),
                (SELECT COUNT(*) FROM [Identity].[Users]),
                (SELECT COUNT(*) FROM [Identity].[Roles]),
                (SELECT COUNT(*) FROM [Security].[SystemSecurityAuditEvents]);
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(2, reader.GetInt32(2));
        Assert.Equal(1, reader.GetInt32(3));
    }
}
