// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Identity;
using Workbench.Server.Security;
using Workbench.Server.Tenancy;
using Workbench.Server.Storage;
using Workbench.Server.Operations;

namespace Workbench.Server.Persistence;

public class WorkbenchDbContext : IdentityDbContext<
    WorkbenchUser,
    WorkbenchRole,
    Guid,
    WorkbenchUserClaim,
    WorkbenchUserRole,
    WorkbenchUserLogin,
    WorkbenchRoleClaim,
    WorkbenchUserToken>, IDataProtectionKeyContext
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

    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<AttachmentRevision> AttachmentRevisions => Set<AttachmentRevision>();
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TenantSecurityAuditEvent> TenantSecurityAuditEvents =>
        Set<TenantSecurityAuditEvent>();

    public DbSet<LoginDirectoryEntry> LoginDirectory => Set<LoginDirectoryEntry>();

    public DbSet<WorkbenchSession> Sessions => Set<WorkbenchSession>();

    public DbSet<IdentityOperation> IdentityOperations => Set<IdentityOperation>();

    public DbSet<SystemSecurityAuditEvent> SystemSecurityAuditEvents => Set<SystemSecurityAuditEvent>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

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
        auditEvent.Property(row => row.TargetType).HasMaxLength(100);
        auditEvent.Property(row => row.Outcome).HasMaxLength(50).HasDefaultValue("Succeeded").IsRequired();
        auditEvent.Property(row => row.CorrelationId).HasMaxLength(100);
        auditEvent.Property(row => row.MetadataJson).HasMaxLength(2000);
        auditEvent.Property(row => row.RowVersion).IsRowVersion();
        auditEvent.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(row => row.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        ConfigureIdentity(modelBuilder);
        ConfigureSessions(modelBuilder);
        ConfigureIdentityOperations(modelBuilder);
        ConfigureSystemAudit(modelBuilder);
        ConfigureStorage(modelBuilder);
        ConfigureWork(modelBuilder);
    }

    private void ConfigureWork(ModelBuilder modelBuilder)
    {
        var work = modelBuilder.Entity<WorkItem>();
        work.ToTable("WorkItems", "Operations", table =>
        {
            table.HasCheckConstraint("CK_WorkItems_Kind", "([Kind] = 1 AND [AttachmentId] IS NOT NULL AND [IdentityOperationId] IS NULL AND [ProtectedPayload] IS NULL) OR ([Kind] = 2 AND [IdentityOperationId] IS NOT NULL AND [AttachmentId] IS NULL)");
            table.HasCheckConstraint("CK_WorkItems_State", "[State] BETWEEN 0 AND 3 AND [Attempts] BETWEEN 0 AND 5 AND [Generation] >= 0");
        });
        work.HasKey(row => row.Id);
        work.IsTenantOwned(row => (Guid?)row.TenantId == TenantContext.TenantId);
        work.Property(row => row.Kind).HasConversion<int>();
        work.Property(row => row.State).HasConversion<int>().HasDefaultValue(WorkState.Ready);
        work.Property(row => row.Attempts).HasDefaultValue(0);
        work.Property(row => row.Generation).HasDefaultValue(0L);
        work.Property(row => row.ProtectedPayload).HasMaxLength(8000);
        work.Property(row => row.Outcome).HasMaxLength(40);
        work.Property(row => row.RowVersion).IsRowVersion();
        work.HasIndex(row => new { row.State, row.AvailableAtUtc });
        work.HasOne<Attachment>().WithMany().HasForeignKey(row => new { row.TenantId, row.AttachmentId })
            .HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);
        work.HasOne<IdentityOperation>().WithMany().HasForeignKey(row => new { row.TenantId, row.IdentityOperationId })
            .HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);
    }

    private void ConfigureStorage(ModelBuilder modelBuilder)
    {
        var attachment = modelBuilder.Entity<Attachment>();
        attachment.ToTable("Attachments", "Storage");
        attachment.HasKey(row => row.Id);
        attachment.IsTenantOwned(row => (Guid?)row.TenantId == TenantContext.TenantId);
        attachment.Property(row => row.Held).HasDefaultValue(false);
        attachment.Property(row => row.RowVersion).IsRowVersion();
        attachment.HasOne<Tenant>().WithMany().HasForeignKey(row => row.TenantId)
            .OnDelete(DeleteBehavior.Restrict);

        var revision = modelBuilder.Entity<AttachmentRevision>();
        revision.ToTable("Revisions", "Storage", table =>
        {
            table.UseSqlOutputClause(false);
            table.HasCheckConstraint("CK_Revisions_Content", "([State] IN (0,2) AND [Length] IS NULL AND [Sha256] IS NULL) OR ([State] IN (1,3) AND [Length] IS NOT NULL AND [Sha256] IS NOT NULL AND [Length] >= 0 AND LEN([Sha256]) = 64 AND [Sha256] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9A-F]%')");
        });
        revision.HasKey(row => row.Id);
        revision.IsTenantOwned(row => (Guid?)row.TenantId == TenantContext.TenantId);
        revision.HasAlternateKey(row => new { row.TenantId, row.AttachmentId, row.Id });
        revision.HasIndex(row => new { row.TenantId, row.OperationId }).IsUnique();
        revision.Property(row => row.ProviderAlias).HasMaxLength(64);
        revision.Property(row => row.Source).HasMaxLength(64);
        revision.Property(row => row.MediaType).HasMaxLength(100);
        revision.Property(row => row.Sha256).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        revision.Property(row => row.State).HasConversion<int>();
        revision.Property(row => row.RowVersion).IsRowVersion();
        revision.HasOne<Attachment>().WithMany()
            .HasForeignKey(row => new { row.TenantId, row.AttachmentId })
            .HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);
        revision.HasOne<AttachmentRevision>().WithMany()
            .HasForeignKey(row => new { row.TenantId, row.AttachmentId, row.PreviousRevisionId })
            .HasPrincipalKey(row => new { row.TenantId, row.AttachmentId, row.Id }).OnDelete(DeleteBehavior.Restrict);
        attachment.HasOne<AttachmentRevision>().WithMany()
            .HasForeignKey(row => new { row.TenantId, row.Id, row.CurrentRevisionId })
            .HasPrincipalKey(row => new { row.TenantId, row.AttachmentId, row.Id }).OnDelete(DeleteBehavior.Restrict);
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

    private void ConfigureSessions(ModelBuilder modelBuilder)
    {
        var session = modelBuilder.Entity<WorkbenchSession>();
        session.ToTable("Sessions", "Identity");
        session.HasKey(row => row.Id);
        session.IsTenantOwned(row => (Guid?)row.TenantId == TenantContext.TenantId);
        session.Property(row => row.TokenHash).HasColumnType("binary(32)").IsRequired();
        session.HasIndex(row => row.TokenHash).IsUnique();
        session.Property(row => row.RevocationReason).HasMaxLength(100);
        session.Property(row => row.RowVersion).IsRowVersion();
        session.HasOne<WorkbenchUser>()
            .WithMany()
            .HasForeignKey(row => new { row.TenantId, row.UserId })
            .HasPrincipalKey(row => new { row.TenantId, row.Id })
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<DataProtectionKey>().ToTable("DataProtectionKeys", "Identity");
    }

    private void ConfigureIdentityOperations(ModelBuilder modelBuilder)
    {
        var operation = modelBuilder.Entity<IdentityOperation>();
        operation.ToTable("IdentityOperations", "Identity");
        operation.HasKey(row => row.Id);
        operation.IsTenantOwned(row => (Guid?)row.TenantId == TenantContext.TenantId);
        operation.Property(row => row.Purpose).HasConversion<int>();
        operation.Property(row => row.TokenHash).HasColumnType("binary(32)").IsRequired();
        operation.HasIndex(row => row.TokenHash).IsUnique();
        operation.Property(row => row.RowVersion).IsRowVersion();
        operation.HasOne<WorkbenchUser>()
            .WithMany()
            .HasForeignKey(row => new { row.TenantId, row.UserId })
            .HasPrincipalKey(row => new { row.TenantId, row.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureSystemAudit(ModelBuilder modelBuilder)
    {
        var audit = modelBuilder.Entity<SystemSecurityAuditEvent>();
        audit.ToTable("SystemSecurityAuditEvents", "Security");
        audit.HasKey(row => row.Id);
        audit.Property(row => row.Action).HasMaxLength(200).IsRequired();
        audit.Property(row => row.Outcome).HasMaxLength(50).IsRequired();
        audit.Property(row => row.CorrelationId).HasMaxLength(100);
        audit.Property(row => row.MetadataJson).HasMaxLength(2000);
        audit.Property(row => row.RowVersion).IsRowVersion();
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
