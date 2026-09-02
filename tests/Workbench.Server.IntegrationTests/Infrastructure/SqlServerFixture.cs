// Copyright (c) 2026 The White Stag Collection.

using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace Workbench.Server.IntegrationTests.Infrastructure;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder(
        "mcr.microsoft.com/mssql/server:2022-CU20-ubuntu-22.04")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

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
            await using var command = new SqlCommand($"CREATE DATABASE [{databaseName}]", connection);
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
