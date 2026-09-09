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
public sealed class DevelopmentDatabaseInspectionDatabaseTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task WorkloadIdentityCannotReportAnRlsFilteredDatabaseAsEmpty()
    {
        // GIVEN a contained workload identity that cannot observe every tenant.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        await using var connection = new SqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("CREATE USER [inspection_workload] WITH PASSWORD='Inspection-password-123!'", connection);
        await command.ExecuteNonQueryAsync();
        var workload = new SqlConnectionStringBuilder(database.AdminConnectionString)
        {
            UserID = "inspection_workload",
            Password = "Inspection-password-123!"
        };

        // WHEN inspection is attempted without the database owner's global view.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DevelopmentDatabaseInspection.InspectAsync(workload.ConnectionString, default));

        // THEN it fails closed instead of encouraging a second bootstrap.
        Assert.Equal("Development inspection requires the database owner.", error.Message);
    }

    [Fact]
    public async Task InspectionTracksInitialProvisioningWithoutChangingTheDatabase()
    {
        // GIVEN an empty database before migration or bootstrap.
        await using var database = await sqlServer.CreateDatabaseAsync();
        var empty = await DevelopmentDatabaseInspection.InspectAsync(database.AdminConnectionString, default);
        Assert.True(empty.MigrationHistoryCompatible);
        Assert.False(empty.SchemaCurrent);
        Assert.False(empty.SchemaInitialized);
        Assert.False(empty.BootstrapCompleted);
        Assert.Empty(empty.AppliedMigrations);
        Assert.Empty(empty.PrincipalRolesPresent);

        // WHEN the existing migrator and atomic bootstrap commands provision it.
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        var migrated = await DevelopmentDatabaseInspection.InspectAsync(database.AdminConnectionString, default);
        Assert.True(migrated.SchemaCurrent);
        Assert.True(migrated.SchemaInitialized);
        Assert.False(migrated.BootstrapCompleted);
        Assert.Equal(3, migrated.PrincipalRolesPresent.Length);
        var commands = new OperatorCommands(database.AdminConnectionString, new PasswordHasher<WorkbenchUser>(), TimeProvider.System);
        await commands.BootstrapAsync("Inspection tenant", "inspect@example.test", "Inspection-password-123!", default);
        var completed = await DevelopmentDatabaseInspection.InspectAsync(database.AdminConnectionString, default);

        // THEN inspection detects the committed bootstrap and leaves migration history intact.
        Assert.True(completed.BootstrapCompleted);
        Assert.Equal(migrated.AppliedMigrations, completed.AppliedMigrations);
        await Assert.ThrowsAsync<BootstrapAlreadyCompletedException>(() =>
            commands.BootstrapAsync("Another tenant", "other@example.test", "Inspection-password-123!", default));
    }

    [Theory]
    [InlineData("INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES ('99999999999999_Future', '10.0.0')")]
    [InlineData("DELETE FROM [__EFMigrationsHistory] WHERE [MigrationId]=(SELECT MIN([MigrationId]) FROM [__EFMigrationsHistory])")]
    public async Task RetainedNewerOrDivergentHistoryIsReportedIncompatible(string change)
    {
        // GIVEN a retained schema with history that the current image cannot advance safely.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        await using var connection = new SqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(change, connection);
        await command.ExecuteNonQueryAsync();

        // WHEN a lifecycle caller inspects it before migration.
        var report = await DevelopmentDatabaseInspection.InspectAsync(database.AdminConnectionString, default);

        // THEN it receives a refusal condition without a migration attempt.
        Assert.False(report.MigrationHistoryCompatible);
        Assert.False(report.SchemaCurrent);
    }
}
