// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Identity;
using Workbench.Server.Purchasing;

namespace Workbench.Server.Persistence;

public partial class WorkbenchDbContext
{
    public DbSet<SupplierBill> SupplierBills => Set<SupplierBill>();
    public DbSet<SupplierBillRevision> SupplierBillRevisions => Set<SupplierBillRevision>();
    public DbSet<SupplierBillReceipt> SupplierBillReceipts => Set<SupplierBillReceipt>();

    private void ConfigureSupplierBills(ModelBuilder builder)
    {
        var bill = builder.Entity<SupplierBill>();
        bill.ToTable("SupplierBills", "Purchasing", t => t.HasCheckConstraint("CK_SupplierBills_State", "[State] COLLATE Latin1_General_100_BIN2 IN ('Draft','Reviewed','Posted','Abandoned')"));
        bill.HasKey(x => new { x.TenantId, x.Id });
        bill.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        bill.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false);
        bill.Property(x => x.State).HasMaxLength(16).IsUnicode(false);
        bill.Property(x => x.RowVersion).IsRowVersion();
        bill.HasOne<DraftOrder>().WithMany().HasForeignKey(x => new { x.TenantId, x.PurchaseOrderId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        bill.HasOne<Supplier>().WithMany().HasForeignKey(x => new { x.TenantId, x.SupplierId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        bill.HasOne<SupplierBillRevision>().WithMany().HasForeignKey(x => new { x.TenantId, x.Id, x.CurrentRevisionId }).HasPrincipalKey(x => new { x.TenantId, x.BillId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        bill.HasIndex(x => new { x.TenantId, x.PurchaseOrderId, x.Id });

        var revision = builder.Entity<SupplierBillRevision>();
        revision.ToTable("SupplierBillRevisions", "Purchasing", t => t.HasCheckConstraint("CK_SupplierBillRevisions_Payload", "[Sequence]>0 AND ISJSON([Payload],OBJECT)=1 AND DATALENGTH([Payload])<=262144"));
        revision.HasKey(x => new { x.TenantId, x.Id });
        revision.HasAlternateKey(x => new { x.TenantId, x.BillId, x.Id });
        revision.HasIndex(x => new { x.TenantId, x.BillId, x.Sequence }).IsUnique();
        revision.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        revision.Property(x => x.SupplierName).HasMaxLength(200);
        revision.Property(x => x.NormalizedReference).HasMaxLength(200).UseCollation("Latin1_General_100_BIN2");
        revision.HasIndex(x => new { x.TenantId, x.NormalizedReference });
        revision.HasOne<SupplierBill>().WithMany().HasForeignKey(x => new { x.TenantId, x.BillId }).OnDelete(DeleteBehavior.Restrict);
        revision.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var receipt = builder.Entity<SupplierBillReceipt>();
        receipt.ToTable("SupplierBillReceipts", "Purchasing", t => t.HasCheckConstraint("CK_SupplierBillReceipts_Json", "ISJSON([CanonicalInput],OBJECT)=1 AND DATALENGTH([CanonicalInput])<=262144 AND ISJSON([ResultJson],OBJECT)=1 AND [RequestId]<>'00000000-0000-0000-0000-000000000000'"));
        receipt.HasKey(x => new { x.TenantId, x.Operation, x.RequestId });
        receipt.Property(x => x.Operation).HasMaxLength(16).IsUnicode(false);
        receipt.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        receipt.HasOne<SupplierBill>().WithMany().HasForeignKey(x => new { x.TenantId, x.BillId }).OnDelete(DeleteBehavior.Restrict);
        receipt.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
