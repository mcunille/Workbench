// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore;

namespace Workbench.Server.Persistence;

public static class DatabaseMigrator
{
    public static async Task MigrateAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlServer(connectionString, sql =>
                sql.MigrationsAssembly(typeof(WorkbenchDbContext).Assembly.FullName))
            .Options;

        await using var database = new WorkbenchDbContext(options);
        await database.Database.MigrateAsync(cancellationToken);
    }
}
