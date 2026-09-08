// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests.Infrastructure;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly Lazy<Task<SchemaTemplate>> _schemaTemplate;
    private readonly MsSqlContainer _container = new MsSqlBuilder(
        "mcr.microsoft.com/mssql/server:2022-CU20-ubuntu-22.04")
        .Build();

    public SqlServerFixture() => _schemaTemplate = new(CreateSchemaTemplateAsync);

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var connection = new SqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            EXEC sp_configure 'show advanced options', 1;
            RECONFIGURE;
            EXEC sp_configure 'contained database authentication', 1;
            RECONFIGURE;
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    // Only ordinary current-schema application fixtures use the template. Migration,
    // permission and recovery drills keep CreateDatabaseAsync and their explicit migrations.
    public async Task<SqlTestDatabase> CreateMigratedDatabaseAsync(string? priorMigration = null)
    {
        if (priorMigration is not null)
        {
            var prior = await CreateDatabaseAsync();
            try
            {
                await DatabaseMigrator.MigrateToAsync(prior.AdminConnectionString, priorMigration, default);
                return prior;
            }
            catch
            {
                await prior.DisposeAsync();
                throw;
            }
        }

        var template = await _schemaTemplate.Value;
        var name = $"workbench_test_{Guid.NewGuid():N}";
        var master = new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = "master" };
        var database = new SqlTestDatabase(name, master.ConnectionString,
            new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = name }.ConnectionString);
        try
        {
            await using var connection = new SqlConnection(master.ConnectionString);
            await connection.OpenAsync();
            await using var restore = new SqlCommand($"""
                RESTORE DATABASE [{name}] FROM DISK = @backup WITH CHECKSUM,
                    MOVE @dataName TO @dataPath, MOVE @logName TO @logPath;
                """, connection) { CommandTimeout = 60 };
            restore.Parameters.AddWithValue("@backup", template.BackupPath);
            restore.Parameters.AddWithValue("@dataName", template.DataName);
            restore.Parameters.AddWithValue("@logName", template.LogName);
            restore.Parameters.AddWithValue("@dataPath", $"/var/opt/mssql/data/{name}.mdf");
            restore.Parameters.AddWithValue("@logPath", $"/var/opt/mssql/data/{name}_log.ldf");
            await restore.ExecuteNonQueryAsync();
            // The migrated template has no users or application data. Each clone also
            // needs its own tenant proof key before any credentials or host are created.
            await using var rekey = new SqlCommand($"UPDATE [{name}].[Security].[TenantContextKeys] SET [ProofKey] = CRYPT_GEN_RANDOM(32) WHERE [Id] = 1", connection);
            if (await rekey.ExecuteNonQueryAsync() != 1)
                throw new InvalidOperationException("The restored test database has no tenant proof key.");
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private async Task<SchemaTemplate> CreateSchemaTemplateAsync()
    {
        await using var database = await CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(database.AdminConnectionString, default);
        var name = new SqlConnectionStringBuilder(database.AdminConnectionString).InitialCatalog;
        var backupPath = $"/var/opt/mssql/data/{name}.bak";
        await using var connection = new SqlConnection(database.AdminConnectionString);
        await connection.OpenAsync();
        string dataName;
        string logName;
        await using (var files = new SqlCommand("SELECT [name] FROM sys.database_files ORDER BY [type]", connection))
        await using (var reader = await files.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new InvalidOperationException("Missing template data file.");
            dataName = reader.GetString(0);
            if (!await reader.ReadAsync()) throw new InvalidOperationException("Missing template log file.");
            logName = reader.GetString(0);
            if (await reader.ReadAsync()) throw new InvalidOperationException("Unexpected template database file.");
        }
        await using var backup = new SqlCommand($"BACKUP DATABASE [{name}] TO DISK = @path WITH COPY_ONLY, INIT, CHECKSUM", connection);
        backup.Parameters.AddWithValue("@path", backupPath);
        await backup.ExecuteNonQueryAsync();
        // Backup storage is inside this fixture's disposable container, never shared across runs.
        return new SchemaTemplate(backupPath, dataName, logName);
    }

    private sealed record SchemaTemplate(string BackupPath, string DataName, string LogName);

    public async Task<SqlTestDatabase> CreateDatabaseAsync()
    {
        var databaseName = $"workbench_test_{Guid.NewGuid():N}";
        var adminBuilder = new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = "master",
        };

        await using (var connection = new SqlConnection(adminBuilder.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new SqlCommand($"""
                CREATE DATABASE [{databaseName}];
                ALTER DATABASE [{databaseName}] SET CONTAINMENT = PARTIAL;
                """, connection);
            await command.ExecuteNonQueryAsync();
        }

        var databaseBuilder = new SqlConnectionStringBuilder(adminBuilder.ConnectionString)
        {
            InitialCatalog = databaseName,
        };

        return new SqlTestDatabase(databaseName, adminBuilder.ConnectionString, databaseBuilder.ConnectionString);
    }
}

public sealed class SqlTestDatabase(
    string databaseName,
    string masterConnectionString,
    string adminConnectionString) : IAsyncDisposable
{
    public string AdminConnectionString { get; } = adminConnectionString;

    public async Task<byte[]> GetTenantContextProofKeyAsync()
    {
        await using var connection = new SqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT [ProofKey] FROM [Security].[TenantContextKeys] WHERE [Id] = 1",
            connection);
        return (byte[])(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The tenant context proof key is missing."));
    }

    public async Task<string> CreateWebUserAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var userName = $"workbench_web_{suffix}";
        var password = $"W0rkbench-{suffix}!";

        await using var connection = new SqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"""
            CREATE USER [{userName}] WITH PASSWORD = '{password}';
            IF DATABASE_PRINCIPAL_ID(N'workbench_web') IS NOT NULL
                ALTER ROLE [workbench_web] ADD MEMBER [{userName}];
            """, connection);
        await command.ExecuteNonQueryAsync();

        return new SqlConnectionStringBuilder(AdminConnectionString)
        {
            UserID = userName,
            Password = password,
            IntegratedSecurity = false,
        }.ConnectionString;
    }

    public async Task<string> CreateRoleUserAsync(string roleName)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var userName = $"workbench_test_{suffix}";
        var password = $"W0rkbench-{suffix}!";

        await using var connection = new SqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"""
            CREATE USER [{userName}] WITH PASSWORD = '{password}';
            ALTER ROLE [{roleName}] ADD MEMBER [{userName}];
            """, connection);
        await command.ExecuteNonQueryAsync();
        return new SqlConnectionStringBuilder(AdminConnectionString)
        {
            UserID = userName,
            Password = password,
            IntegratedSecurity = false,
        }.ConnectionString;
    }

    public async Task SeedTenantAuditRowsAsync(Guid tenantA, Guid tenantB)
    {
        await using var connection = new SqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            INSERT INTO [Tenancy].[Tenants]
                ([Id], [Name], [NormalizedName], [IsEnabled], [CreatedAtUtc])
            VALUES
                (@tenantA, N'Tenant A', N'TENANT A', 1, SYSUTCDATETIME()),
                (@tenantB, N'Tenant B', N'TENANT B', 1, SYSUTCDATETIME());

            INSERT INTO [Security].[TenantSecurityAuditEvents]
                ([Id], [TenantId], [Action], [OccurredAtUtc])
            VALUES
                (NEWID(), @tenantA, N'test.seeded', SYSUTCDATETIME()),
                (NEWID(), @tenantB, N'test.seeded', SYSUTCDATETIME());
            """, connection);
        command.Parameters.Add(new SqlParameter("@tenantA", SqlDbType.UniqueIdentifier) { Value = tenantA });
        command.Parameters.Add(new SqlParameter("@tenantB", SqlDbType.UniqueIdentifier) { Value = tenantB });
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(masterConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"IF DB_ID(@name) IS NOT NULL BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END",
            connection);
        command.Parameters.AddWithValue("@name", databaseName);
        await command.ExecuteNonQueryAsync();
    }
}
