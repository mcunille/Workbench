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
    [Theory]
    [InlineData("alllowercasepassword")]
    [InlineData("No-Symbols-Or-Digits")]
    [InlineData("Short-4!")]
    public async Task BootstrapRejectsPasswordsOutsideTheApplicationPolicy(string password)
    {
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var commands = new OperatorCommands(
            database.AdminConnectionString,
            new PasswordHasher<WorkbenchUser>(),
            TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentException>(() => commands.BootstrapAsync(
            "First Tenant",
            "admin@example.com",
            password,
            CancellationToken.None));
    }

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

    [Fact]
    public async Task DevelopmentRecoveryReturnsRawTokenOnlyToDatabaseOwnerCaller()
    {
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);
        var commands = new OperatorCommands(
            database.AdminConnectionString,
            new PasswordHasher<WorkbenchUser>(),
            TimeProvider.System);
        await commands.BootstrapAsync(
            "Recovery Tenant",
            "recovery-admin@example.com",
            "Correct Horse Battery Staple 3#",
            CancellationToken.None);

        var token = await commands.CreateDevelopmentRecoveryAsync(
            "recovery-admin@example.com",
            CancellationToken.None);

        Assert.NotNull(token);
        await using var connection = new SqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT [TokenHash] FROM [Identity].[IdentityOperations]",
            connection);
        var stored = Assert.IsType<byte[]>(await command.ExecuteScalarAsync());
        Assert.Equal(SessionToken.Hash(token), stored);
    }
}
