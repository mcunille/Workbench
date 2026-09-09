// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore;
using Workbench.Server.Inventory;
using Workbench.Server.Tenancy;

namespace Workbench.Server.Persistence;

public partial class WorkbenchDbContext
{
    // Explicit null branches keep SQL CHECK's UNKNOWN result from accepting incomplete dates.
    internal const string DateConstraint = "([Year] IS NULL AND [Month] IS NULL AND [Day] IS NULL) OR ([Year] IS NOT NULL AND [Year] BETWEEN 1 AND 9999 AND (([Month] IS NULL AND [Day] IS NULL) OR ([Month] IS NOT NULL AND [Month] BETWEEN 1 AND 12 AND ([Day] IS NULL OR ([Day] BETWEEN 1 AND 31 AND TRY_CONVERT(date, CONCAT(RIGHT('0000'+CONVERT(varchar(4),[Year]),4), RIGHT('00'+CONVERT(varchar(2),[Month]),2), RIGHT('00'+CONVERT(varchar(2),[Day]),2)),112) IS NOT NULL)))))";
    internal const string MethodConstraint = "[Method] COLLATE Latin1_General_100_BIN2 IN ('Purchase','Gift','Inheritance','Trade','Other','Unknown') AND DATALENGTH([Method]) = DATALENGTH(RTRIM([Method]))";

    private void ConfigureAcquisitions(ModelBuilder modelBuilder)
    {
        var acquisition = modelBuilder.Entity<Acquisition>();
        acquisition.ToTable("Acquisitions", "Inventory", table =>
        {
            table.HasCheckConstraint("CK_Acquisitions_Method", MethodConstraint);
            table.HasCheckConstraint("CK_Acquisitions_Date", DateConstraint);
            table.HasCheckConstraint("CK_Acquisitions_Request", "[CreationRequestId] <> '00000000-0000-0000-0000-000000000000'");
        });
        acquisition.HasKey(row => row.Id);
        acquisition.IsTenantOwned(row => (Guid?)row.TenantId == TenantContext.TenantId);
        acquisition.HasIndex(row => new { row.TenantId, row.CreationRequestId }).IsUnique();
        acquisition.Property(row => row.Method).HasMaxLength(16).IsRequired();
        acquisition.Property(row => row.Source).HasMaxLength(200);
        acquisition.Property(row => row.Notes).HasMaxLength(4000);
        acquisition.Property(row => row.RowVersion).IsRowVersion();
        acquisition.HasOne<Tenant>().WithMany().HasForeignKey(row => row.TenantId).OnDelete(DeleteBehavior.Restrict);

        var link = modelBuilder.Entity<AcquisitionItem>();
        link.ToTable("AcquisitionItems", "Inventory");
        link.HasKey(row => new { row.TenantId, row.ItemId });
        link.HasQueryFilter(row => (Guid?)row.TenantId == TenantContext.TenantId);
        link.HasOne<InventoryItem>().WithMany().HasForeignKey(row => new { row.TenantId, row.ItemId })
            .HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);
        link.HasOne(row => row.Acquisition).WithMany().HasForeignKey(row => new { row.TenantId, row.AcquisitionId })
            .HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);

        var creation = modelBuilder.Entity<AcquisitionCreationRecord>();
        creation.ToTable("AcquisitionCreationRecords", "Inventory", table =>
        {
            table.HasCheckConstraint("CK_AcquisitionCreationRecords_Method", MethodConstraint);
            table.HasCheckConstraint("CK_AcquisitionCreationRecords_Date", DateConstraint);
        });
        creation.HasKey(row => new { row.TenantId, row.CreationRequestId });
        creation.HasQueryFilter(row => (Guid?)row.TenantId == TenantContext.TenantId);
        creation.Property(row => row.Method).HasMaxLength(16).IsRequired();
        creation.Property(row => row.Source).HasMaxLength(200);
        creation.Property(row => row.Notes).HasMaxLength(4000);
        creation.HasOne<InventoryItem>().WithMany().HasForeignKey(row => new { row.TenantId, row.ItemId })
            .HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);
        creation.HasOne<Acquisition>().WithMany().HasForeignKey(row => new { row.TenantId, row.AcquisitionId })
            .HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
