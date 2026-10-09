// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Workbench.Server.Persistence;

// System key storage has no tenant authority, including during a cold admin sign-in.
public sealed class DataProtectionKeyDbContext(DbContextOptions<DataProtectionKeyDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<DataProtectionKey>().ToTable("DataProtectionKeys", "Identity");
}
