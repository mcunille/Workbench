// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore;
using Workbench.Server.Persistence;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class CurrentSchemaTests
{
    [Fact]
    public void ReleaseContractMatchesTheCompleteEfMigrationInventory()
    {
        // GIVEN the explicit release contract and the actual compiled EF migration inventory.
        using var database = new WorkbenchDbContext(new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlServer("Server=unused;Database=inventory-only;Integrated Security=true").Options);
        // WHEN comparing in order THEN no missing, extra, or renamed migration is hidden by a count.
        Assert.Equal(CurrentSchema.Migrations, database.Database.GetMigrations());
        Assert.Equal(CurrentSchema.Migrations.Order(StringComparer.Ordinal), CurrentSchema.Migrations);
        Assert.Equal(CurrentSchema.Migrations.Distinct(StringComparer.Ordinal), CurrentSchema.Migrations);
        Assert.Equal(CurrentSchema.Migrations[^1], CurrentSchema.MigrationId);
    }
}
