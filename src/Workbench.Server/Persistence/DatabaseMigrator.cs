// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

public static class DatabaseMigrator
{
    public static async Task MigrateAsync(
        string connectionString,
        CancellationToken cancellationToken) =>
        await MigrateToAsync(connectionString, null, cancellationToken);

    public static async Task MigrateToAsync(
        string connectionString,
        string? targetMigration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlServer(connectionString, sql =>
                sql.MigrationsAssembly(typeof(WorkbenchDbContext).Assembly.FullName))
            .Options;

        await using var database = new WorkbenchDbContext(options);
        var migrator = database.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration, cancellationToken);
    }
}
