// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Workbench.Server.IntegrationTests.Infrastructure;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class DatabaseMigrationTests(SqlServerFixture sqlServer)
{
    [Fact]
    public async Task MigratorCreatesCurrentSchemaOnEmptyDatabase()
    {
        // GIVEN an empty database.
        await using var database = await sqlServer.CreateDatabaseAsync();

        // WHEN the complete release schema is applied.
        await DatabaseMigrator.MigrateAsync(
            database.AdminConnectionString,
            CancellationToken.None);

        await using var connection = new SqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT [MigrationId] FROM [dbo].[__EFMigrationsHistory] ORDER BY [MigrationId]",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var migrations = new List<string>();
        while (await reader.ReadAsync())
        {
            migrations.Add(reader.GetString(0));
        }

        // THEN this feature adds one migration after the established schema history.
        Assert.Collection(
            migrations,
            migration => Assert.EndsWith("_InitialSchema", migration, StringComparison.Ordinal),
            migration => Assert.EndsWith("_EstablishSecurityBoundaries", migration, StringComparison.Ordinal),
            migration => Assert.EndsWith("_AddBlobAndOperationalProviders", migration, StringComparison.Ordinal),
            migration => Assert.EndsWith("_AddDeploymentQueueTelemetry", migration, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("InitialSchema")]
    [InlineData("EstablishSecurityBoundaries")]
    [InlineData("AddBlobAndOperationalProviders")]
    public async Task MigratorUpgradesASeededPriorSchemaWithoutLosingTenantData(string priorMigration)
    {
        // GIVEN tenant data in either the initial schema or the PR base schema.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateToAsync(
            database.AdminConnectionString,
            priorMigration,
            CancellationToken.None);
        await using (var connection = new SqlConnection(database.AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var seed = new SqlCommand("""
                INSERT INTO [Tenancy].[Tenants]
                    ([Id], [Name], [NormalizedName], [IsEnabled], [CreatedAtUtc])
                VALUES
                    ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', N'Upgrade Tenant',
                     N'UPGRADE TENANT', 1, SYSUTCDATETIME())
                """, connection);
            await seed.ExecuteNonQueryAsync();
        }

        // WHEN the current release is applied.
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);

        // THEN existing tenant data survives the upgrade.
        Assert.Equal(1, await CountAsync(database.AdminConnectionString, "[Tenancy].[Tenants]"));
    }

    [Fact]
    public async Task RetainedMetadataCannotBeRolledBackDestructively()
    {
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, CancellationToken.None);

        // GIVEN durable storage metadata, WHEN a destructive schema rollback is requested,
        // THEN it is refused and the current tables remain available for offline recovery.
        var error = await Assert.ThrowsAsync<SqlException>(() => DatabaseMigrator.MigrateToAsync(
            database.AdminConnectionString,
            "InitialSchema",
            CancellationToken.None));
        Assert.Equal(50020, error.Number);
        Assert.Equal(1, await ObjectCountAsync(database.AdminConnectionString, "Storage.Revisions"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("InitialSchema")]
    public async Task ConcurrentMigratorsSerializeAndApplyEachMigrationOnce(string? priorMigration)
    {
        // GIVEN an empty or prior-release database and an independently held migration lock.
        await using var database = await sqlServer.CreateDatabaseAsync();
        if (priorMigration is not null)
        {
            await DatabaseMigrator.MigrateToAsync(database.AdminConnectionString, priorMigration, CancellationToken.None);
        }
        await using var lockConnection = new SqlConnection(database.AdminConnectionString);
        await lockConnection.OpenAsync();
        await SetMigrationLockAsync(lockConnection, acquire: true);
        var application = $"migration-concurrency-{Guid.NewGuid():N}";
        var connectionString = new SqlConnectionStringBuilder(database.AdminConnectionString)
        {
            ApplicationName = application,
            Pooling = false,
        }.ConnectionString;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task MigrateAsync()
        {
            await start.Task;
            await DatabaseMigrator.MigrateAsync(connectionString, timeout.Token);
        }
        var first = MigrateAsync();
        var second = MigrateAsync();

        // WHEN both independent migrators start together and demonstrably wait for the SQL lock.
        start.SetResult();
        await WaitForMigrationWaitersAsync(lockConnection, application, 2, timeout.Token);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        await SetMigrationLockAsync(lockConnection, acquire: false);
        await Task.WhenAll(first, second);

        // THEN both complete successfully, history appears once, and the current schema exists.
        Assert.Equal(4, await CountAsync(database.AdminConnectionString, "[dbo].[__EFMigrationsHistory]"));
        Assert.Equal(1, await ObjectCountAsync(database.AdminConnectionString, "Storage.Revisions"));
        Assert.Equal(1, await ObjectCountAsync(database.AdminConnectionString, "Operations.WorkItems"));
        // AND another invocation observes the completed schema without applying it again.
        await DatabaseMigrator.MigrateAsync(connectionString, timeout.Token);
        Assert.Equal(4, await CountAsync(database.AdminConnectionString, "[dbo].[__EFMigrationsHistory]"));
    }

    [Fact]
    public async Task WaitingMigratorCanBeCancelledWithoutChangingSchemaOrLeakingItsLock()
    {
        // GIVEN a prior schema with its migration lock owned by a different SQL session.
        await using var database = await sqlServer.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateToAsync(database.AdminConnectionString, "InitialSchema", CancellationToken.None);
        await using var lockConnection = new SqlConnection(database.AdminConnectionString);
        await lockConnection.OpenAsync();
        await SetMigrationLockAsync(lockConnection, acquire: true);
        var application = $"migration-cancellation-{Guid.NewGuid():N}";
        var connectionString = new SqlConnectionStringBuilder(database.AdminConnectionString)
        {
            ApplicationName = application,
            Pooling = false,
        }.ConnectionString;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var pending = DatabaseMigrator.MigrateAsync(connectionString, cancellation.Token);
        await WaitForMigrationWaitersAsync(lockConnection, application, 1, cancellation.Token);

        // WHEN the waiting deployment is cancelled before it acquires the lock.
        cancellation.Cancel();
        var failure = await Record.ExceptionAsync(() => pending);
        // SqlClient may surface an attention packet as SqlException instead of task cancellation.
        Assert.True(failure is OperationCanceledException or SqlException);
        if (failure is SqlException sqlFailure)
        {
            Assert.Contains(sqlFailure.Errors.Cast<SqlError>(), error => error.Number == 0 &&
                error.Message.Contains("Operation cancelled by user", StringComparison.Ordinal));
        }

        // THEN no migration was applied and a later invocation can acquire the released lock and upgrade.
        Assert.Equal(1, await CountAsync(database.AdminConnectionString, "[dbo].[__EFMigrationsHistory]"));
        Assert.Equal(0, await ObjectCountAsync(database.AdminConnectionString, "Storage.Revisions"));
        await SetMigrationLockAsync(lockConnection, acquire: false);
        using var retryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await DatabaseMigrator.MigrateAsync(connectionString, retryTimeout.Token);
        Assert.Equal(4, await CountAsync(database.AdminConnectionString, "[dbo].[__EFMigrationsHistory]"));
        Assert.Equal(1, await ObjectCountAsync(database.AdminConnectionString, "Storage.Revisions"));
    }

    private static async Task SetMigrationLockAsync(SqlConnection connection, bool acquire)
    {
        await using var command = new SqlCommand(acquire
            ? "DECLARE @result int; EXEC @result = sp_getapplock @Resource=N'__EFMigrationsLock', @LockOwner=N'Session', @LockMode=N'Exclusive', @LockTimeout=0; SELECT @result;"
            : "DECLARE @result int; EXEC @result = sp_releaseapplock @Resource=N'__EFMigrationsLock', @LockOwner=N'Session'; SELECT @result;", connection);
        Assert.True(Convert.ToInt32(await command.ExecuteScalarAsync()) >= 0);
    }

    private static async Task WaitForMigrationWaitersAsync(SqlConnection connection, string application, int expected, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            await using var command = new SqlCommand("""
                SELECT COUNT(*) FROM sys.dm_tran_locks AS locks
                INNER JOIN sys.dm_exec_sessions AS sessions ON sessions.session_id = locks.request_session_id
                WHERE locks.resource_type = 'APPLICATION' AND locks.request_status = 'WAIT'
                  AND locks.resource_database_id = DB_ID() AND sessions.program_name = @application
                """, connection);
            command.Parameters.AddWithValue("@application", application);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(deadline.Token)) == expected)
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
        }
    }

    private static async Task<int> CountAsync(string connectionString, string table)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"SELECT COUNT(*) FROM {table}", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> ObjectCountAsync(string connectionString, string name)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(@name)", connection);
        command.Parameters.AddWithValue("@name", name);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
