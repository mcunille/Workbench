// Copyright (c) 2026 The White Stag Collection.

using System.Data;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace Workbench.Server.IntegrationTests.Infrastructure;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder(
        "mcr.microsoft.com/mssql/server:2022-CU20-ubuntu-22.04")
        .Build();

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
            $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]",
            connection);
        await command.ExecuteNonQueryAsync();
    }
}
