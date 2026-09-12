// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Identity;
using Workbench.Server.Purchasing;
using Workbench.Server.Tenancy;
namespace Workbench.Server.Persistence;

public partial class WorkbenchDbContext
{
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<SupplierRequestReceipt> SupplierRequestReceipts => Set<SupplierRequestReceipt>();
    public DbSet<PurchaseOrderCounter> PurchaseOrderCounters => Set<PurchaseOrderCounter>();
    private void ConfigureSuppliers(ModelBuilder builder)
    {
        var supplier = builder.Entity<Supplier>();
        supplier.ToTable("Suppliers", "Purchasing", table =>
        {
            table.HasCheckConstraint("CK_Suppliers_Id", "[Id]<>'00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_Suppliers_Timestamps", "[UpdatedAtUtc]>=[CreatedAtUtc] AND DATEPART(TZOFFSET,[CreatedAtUtc])=0 AND DATEPART(TZOFFSET,[UpdatedAtUtc])=0");
        });
        supplier.HasKey(row => row.Id);
        supplier.IsTenantOwned(row => (Guid?)row.TenantId == TenantContext.TenantId);
        supplier.Property(row => row.Name).HasMaxLength(200);
        supplier.Property(row => row.ContactName).HasMaxLength(200);
        supplier.Property(row => row.Email).HasMaxLength(254);
        supplier.Property(row => row.Phone).HasMaxLength(100);
        supplier.Property(row => row.Website).HasMaxLength(2048);
        supplier.Property(row => row.PostalAddress).HasMaxLength(2000);
        supplier.Property(row => row.RowVersion).IsRowVersion();
        supplier.HasIndex(row => new { row.TenantId, row.UpdatedAtUtc, row.Id }).IsDescending(false, true, true);
        supplier.HasOne<Tenant>().WithMany().HasForeignKey(row => row.TenantId).OnDelete(DeleteBehavior.Restrict);
        supplier.HasOne<WorkbenchUser>().WithMany().HasForeignKey(row => new { row.TenantId, row.CreatedByUserId }).HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);
        supplier.HasOne<WorkbenchUser>().WithMany().HasForeignKey(row => new { row.TenantId, row.UpdatedByUserId }).HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);
        var counter = builder.Entity<PurchaseOrderCounter>();
        counter.ToTable("PurchaseOrderCounters", "Purchasing", table => table.HasCheckConstraint("CK_PurchaseOrderCounters_Number", "[LastNumber]>=1")); counter.HasKey(row => row.TenantId);
        counter.HasQueryFilter(row => (Guid?)row.TenantId == TenantContext.TenantId);
        counter.HasOne<Tenant>().WithMany().HasForeignKey(row => row.TenantId).OnDelete(DeleteBehavior.Restrict);
        var receipt = builder.Entity<SupplierRequestReceipt>();
        receipt.ToTable("SupplierRequestReceipts", "Purchasing", table =>
        {
            table.HasCheckConstraint("CK_SupplierRequestReceipts_RequestId", "[RequestId]<>'00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_SupplierRequestReceipts_Operation", "([Operation] COLLATE Latin1_General_100_BIN2='Create' AND [ExpectedRowVersion] IS NULL) OR ([Operation] COLLATE Latin1_General_100_BIN2 IN ('Update','Archive') AND [ExpectedRowVersion] IS NOT NULL)");
            table.HasCheckConstraint("CK_SupplierRequestReceipts_Completed", "DATEPART(TZOFFSET,[CompletedAtUtc])=0");
        }); receipt.HasKey(row => new { row.TenantId, row.RequestId });
        receipt.HasQueryFilter(row => (Guid?)row.TenantId == TenantContext.TenantId);
        receipt.Property(row => row.Operation).HasMaxLength(7).IsUnicode(false);
        receipt.Property(row => row.ExpectedRowVersion).HasColumnType("binary(8)");
        receipt.Property(row => row.ResultRowVersion).HasColumnType("binary(8)");
        receipt.Property(row => row.InputFingerprint).HasColumnType("binary(32)");
        receipt.HasOne<Supplier>().WithMany().HasForeignKey(row => new { row.TenantId, row.SupplierId }).HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);
        receipt.HasOne<WorkbenchUser>().WithMany().HasForeignKey(row => new { row.TenantId, row.ActorUserId }).HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);
        var draft = builder.Entity<DraftOrder>();
        draft.Property(row => row.SupplierContactName).HasMaxLength(200);
        draft.Property(row => row.SupplierEmail).HasMaxLength(254);
        draft.Property(row => row.SupplierPhone).HasMaxLength(100);
        draft.Property(row => row.SupplierWebsite).HasMaxLength(2048);
        draft.Property(row => row.SupplierPostalAddress).HasMaxLength(2000);
        draft.Property(row => row.SupplierOrderReference).HasMaxLength(200);
        draft.Property(row => row.Platform).HasMaxLength(200);
        draft.HasIndex(row => new { row.TenantId, row.PoNumber }).IsUnique().HasFilter("[PoNumber] IS NOT NULL");
        draft.HasOne<Supplier>().WithMany().HasForeignKey(row => new { row.TenantId, row.SupplierId }).HasPrincipalKey(row => new { row.TenantId, row.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
