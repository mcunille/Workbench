// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Workbench.Server.Persistence;

public sealed class WorkbenchDbContextFactory : IDesignTimeDbContextFactory<WorkbenchDbContext>
{
    public WorkbenchDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("WORKBENCH_MIGRATION_CONNECTION")
            ?? "Server=127.0.0.1,1433;Database=workbench_design;User Id=sa;Password=Design-time-only-Password1!;Encrypt=False";

        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlServer(connectionString, sql =>
                sql.MigrationsAssembly(typeof(WorkbenchDbContext).Assembly.FullName))
            .Options;

        return new WorkbenchDbContext(options);
    }
}
