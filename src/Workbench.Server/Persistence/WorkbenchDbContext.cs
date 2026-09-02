// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Identity;
using Workbench.Server.Security;
using Workbench.Server.Tenancy;

namespace Workbench.Server.Persistence;

public class WorkbenchDbContext : IdentityDbContext<
    WorkbenchUser,
    WorkbenchRole,
    Guid,
    WorkbenchUserClaim,
    WorkbenchUserRole,
    WorkbenchUserLogin,
    WorkbenchRoleClaim,
    WorkbenchUserToken>
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

    public DbSet<LoginDirectoryEntry> LoginDirectory => Set<LoginDirectoryEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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

        ConfigureIdentity(modelBuilder);
    }

    private void ConfigureIdentity(ModelBuilder modelBuilder)
    {
        var user = modelBuilder.Entity<WorkbenchUser>();
        user.ToTable("Users", "Identity");
        user.IsTenantOwned(row => (Guid?)row.TenantId == TenantContext.TenantId);
        user.Property(row => row.SecurityVersion).HasDefaultValue(1L);
        user.Property(row => row.State).HasConversion<int>().HasDefaultValue(AccountState.Enabled);
        user.Property(row => row.RowVersion).IsRowVersion();
        user.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(row => row.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        var role = modelBuilder.Entity<WorkbenchRole>();
        role.ToTable("Roles", "Identity");
        role.IsTenantOwned(row => (Guid?)row.TenantId == TenantContext.TenantId);
        role.Property(row => row.RowVersion).IsRowVersion();
        role.HasIndex(row => row.NormalizedName).IsUnique(false).HasDatabaseName("RoleNameIndex");
        role.HasIndex(row => new { row.TenantId, row.NormalizedName })
            .IsUnique()
            .HasDatabaseName("RoleNamePerTenantIndex");
        role.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(row => row.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        ConfigureUserClaims(modelBuilder);
        ConfigureUserRoles(modelBuilder);
        ConfigureUserLogins(modelBuilder);
        ConfigureRoleClaims(modelBuilder);
        ConfigureUserTokens(modelBuilder);

        var directory = modelBuilder.Entity<LoginDirectoryEntry>();
        directory.ToTable("LoginDirectory", "Identity");
        directory.HasKey(row => row.NormalizedEmail);
        directory.Property(row => row.NormalizedEmail).HasMaxLength(256);
        directory.HasIndex(row => new { row.TenantId, row.UserId }).IsUnique();
        directory.HasOne<WorkbenchUser>()
            .WithOne()
            .HasForeignKey<LoginDirectoryEntry>(row => new { row.TenantId, row.UserId })
            .HasPrincipalKey<WorkbenchUser>(row => new { row.TenantId, row.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }

    private void ConfigureUserClaims(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<WorkbenchUserClaim>();
        entity.ToTable("UserClaims", "Identity");
        entity.IsTenantOwned(row => (Guid?)row.TenantId == TenantContext.TenantId);
        entity.HasOne<WorkbenchUser>()
            .WithMany()
            .HasForeignKey(row => new { row.TenantId, row.UserId })
            .HasPrincipalKey(row => new { row.TenantId, row.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }

    private void ConfigureUserRoles(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<WorkbenchUserRole>();
        entity.ToTable("UserRoles", "Identity");
        entity.Property(row => row.TenantId).IsRequired();
        entity.HasKey(row => new { row.TenantId, row.UserId, row.RoleId });
        entity.HasQueryFilter("TenantFilter", row => (Guid?)row.TenantId == TenantContext.TenantId);
        entity.HasOne<WorkbenchUser>()
            .WithMany()
            .HasForeignKey(row => new { row.TenantId, row.UserId })
            .HasPrincipalKey(row => new { row.TenantId, row.Id })
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<WorkbenchRole>()
            .WithMany()
            .HasForeignKey(row => new { row.TenantId, row.RoleId })
            .HasPrincipalKey(row => new { row.TenantId, row.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }

    private void ConfigureUserLogins(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<WorkbenchUserLogin>();
        entity.ToTable("UserLogins", "Identity");
        entity.Property(row => row.TenantId).IsRequired();
        entity.HasKey(row => new { row.TenantId, row.LoginProvider, row.ProviderKey });
        entity.HasQueryFilter("TenantFilter", row => (Guid?)row.TenantId == TenantContext.TenantId);
        entity.HasOne<WorkbenchUser>()
            .WithMany()
            .HasForeignKey(row => new { row.TenantId, row.UserId })
            .HasPrincipalKey(row => new { row.TenantId, row.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }

    private void ConfigureRoleClaims(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<WorkbenchRoleClaim>();
        entity.ToTable("RoleClaims", "Identity");
        entity.IsTenantOwned(row => (Guid?)row.TenantId == TenantContext.TenantId);
        entity.HasOne<WorkbenchRole>()
            .WithMany()
            .HasForeignKey(row => new { row.TenantId, row.RoleId })
            .HasPrincipalKey(row => new { row.TenantId, row.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }

    private void ConfigureUserTokens(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<WorkbenchUserToken>();
        entity.ToTable("UserTokens", "Identity");
        entity.Property(row => row.TenantId).IsRequired();
        entity.HasKey(row => new { row.TenantId, row.UserId, row.LoginProvider, row.Name });
        entity.HasQueryFilter("TenantFilter", row => (Guid?)row.TenantId == TenantContext.TenantId);
        entity.HasOne<WorkbenchUser>()
            .WithMany()
            .HasForeignKey(row => new { row.TenantId, row.UserId })
            .HasPrincipalKey(row => new { row.TenantId, row.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
