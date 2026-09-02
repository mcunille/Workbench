// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore;
using Workbench.Server.Security;
using Workbench.Server.Tenancy;

namespace Workbench.Server.Persistence;

public class WorkbenchDbContext : DbContext
{
    public WorkbenchDbContext(DbContextOptions<WorkbenchDbContext> options)
        : this(options, TenantContext.None)
    {
    }

    public WorkbenchDbContext(
        DbContextOptions<WorkbenchDbContext> options,
        TenantContext tenantContext)
        : base(options)
    {
        TenantContext = tenantContext;
    }

    public TenantContext TenantContext { get; }

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TenantSecurityAuditEvent> TenantSecurityAuditEvents =>
        Set<TenantSecurityAuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var tenant = modelBuilder.Entity<Tenant>();
        tenant.ToTable("Tenants", "Tenancy");
        tenant.HasKey(row => row.Id);
        tenant.Property(row => row.Name).HasMaxLength(200).IsRequired();
        tenant.Property(row => row.NormalizedName).HasMaxLength(200).IsRequired();
        tenant.HasIndex(row => row.NormalizedName).IsUnique();
        tenant.Property(row => row.RowVersion).IsRowVersion();
        tenant.HasQueryFilter(
            "TenantFilter",
            row => (Guid?)row.Id == TenantContext.TenantId);

        var auditEvent = modelBuilder.Entity<TenantSecurityAuditEvent>();
        auditEvent.ToTable("TenantSecurityAuditEvents", "Security");
        auditEvent.HasKey(row => row.Id);
        auditEvent.IsTenantOwned(
            row => (Guid?)row.TenantId == TenantContext.TenantId);
        auditEvent.Property(row => row.Action).HasMaxLength(200).IsRequired();
        auditEvent.Property(row => row.RowVersion).IsRowVersion();
        auditEvent.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(row => row.TenantId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
