// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore;
using Workbench.Server.Gemology;
using Workbench.Server.Identity;
using Workbench.Server.Tenancy;

namespace Workbench.Server.Persistence;

public partial class WorkbenchDbContext
{
    public DbSet<GemReferenceTenantEntry> GemReferenceTenantEntries => Set<GemReferenceTenantEntry>();
    public DbSet<GemReferenceTenantOverride> GemReferenceTenantOverrides => Set<GemReferenceTenantOverride>();

    private void ConfigureGemReferenceTenant(ModelBuilder builder)
    {
        var entry = builder.Entity<GemReferenceTenantEntry>();
        entry.ToTable("TenantEntries", "Gemology", table =>
        {
            table.HasCheckConstraint("CK_GemTenantEntries_Id", "[Id]<>'00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_GemTenantEntries_Content", "ISJSON([ContentJson],OBJECT)=1 AND DATALENGTH([ContentJson])<=2097152");
        });
        entry.HasKey(row => new { row.TenantId, row.Id });
        entry.HasQueryFilter("TenantFilter", row => (Guid?)row.TenantId == TenantContext.TenantId);
        entry.Property(row => row.ContentJson).IsRequired();
        entry.Property(row => row.RowVersion).IsRowVersion();
        entry.HasOne<Tenant>().WithMany().HasForeignKey(row => row.TenantId).OnDelete(DeleteBehavior.Restrict);
        entry.HasOne<WorkbenchUser>().WithMany().HasForeignKey(row => new { row.TenantId, row.CreatedBy })
            .HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);
        entry.HasOne<WorkbenchUser>().WithMany().HasForeignKey(row => new { row.TenantId, row.UpdatedBy })
            .HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);

        var overrides = builder.Entity<GemReferenceTenantOverride>();
        overrides.ToTable("TenantOverrides", "Gemology", table =>
        {
            table.HasCheckConstraint("CK_GemTenantOverrides_Content", "ISJSON([OverridesJson],OBJECT)=1 AND DATALENGTH([OverridesJson])<=2097152");
        });
        overrides.HasKey(row => new { row.TenantId, row.EntryId });
        overrides.HasQueryFilter("TenantFilter", row => (Guid?)row.TenantId == TenantContext.TenantId);
        overrides.Property(row => row.OverridesJson).IsRequired();
        overrides.Property(row => row.RowVersion).IsRowVersion();
        overrides.HasOne<GemReferenceEntry>().WithMany().HasForeignKey(row => row.EntryId).OnDelete(DeleteBehavior.Restrict);
        overrides.HasOne<Tenant>().WithMany().HasForeignKey(row => row.TenantId).OnDelete(DeleteBehavior.Restrict);
        overrides.HasOne<WorkbenchUser>().WithMany().HasForeignKey(row => new { row.TenantId, row.CreatedBy })
            .HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);
        overrides.HasOne<WorkbenchUser>().WithMany().HasForeignKey(row => new { row.TenantId, row.UpdatedBy })
            .HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
