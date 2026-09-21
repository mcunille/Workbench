// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Accounting;
using Workbench.Server.Identity;
using Workbench.Server.Tenancy;
namespace Workbench.Server.Persistence;

public partial class WorkbenchDbContext
{
    public DbSet<AccountingAccount> AccountingAccounts => Set<AccountingAccount>();
    public DbSet<AccountingConfigurationRow> AccountingConfigurations => Set<AccountingConfigurationRow>();
    public DbSet<AccountingRevision> AccountingRevisions => Set<AccountingRevision>();
    public DbSet<AccountingReceipt> AccountingReceipts => Set<AccountingReceipt>();
    private void ConfigureAccounting(ModelBuilder builder)
    {
        var account = builder.Entity<AccountingAccount>();
        account.ToTable("Accounts", "Accounting");
        account.HasKey(x => x.Id);
        account.HasAlternateKey(x => new { x.TenantId, x.Id });
        account.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        account.Property(x => x.Code).HasMaxLength(32).UseCollation("Latin1_General_100_BIN2");
        account.Property(x => x.Name).HasMaxLength(160);
        account.Property(x => x.Type).HasMaxLength(20);
        account.Property(x => x.Purpose).HasMaxLength(40);
        account.Property(x => x.Description).HasMaxLength(2000);
        account.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        account.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        var config = builder.Entity<AccountingConfigurationRow>();
        config.ToTable("Configurations", "Accounting");
        config.HasKey(x => x.TenantId);
        config.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        config.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        var revision = builder.Entity<AccountingRevision>();
        revision.ToTable("Revisions", "Accounting");
        revision.HasKey(x => x.Id);
        revision.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        revision.Property(x => x.Operation).HasMaxLength(20);
        revision.HasIndex(x => new { x.TenantId, x.RecordedAtUtc });
        revision.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        revision.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        revision.HasOne<AccountingAccount>().WithMany().HasForeignKey(x => new { x.TenantId, x.AccountId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        var receipt = builder.Entity<AccountingReceipt>();
        receipt.ToTable("Receipts", "Accounting");
        receipt.HasKey(x => new { x.TenantId, x.RequestId });
        receipt.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        receipt.Property(x => x.Operation).HasMaxLength(20);
        receipt.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        receipt.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        receipt.HasOne<AccountingAccount>().WithMany().HasForeignKey(x => new { x.TenantId, x.AccountId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

