// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Accounting;
using Workbench.Server.Identity;
using Workbench.Server.Purchasing;

namespace Workbench.Server.Persistence;

public partial class WorkbenchDbContext
{
    public DbSet<SupplierPayment> SupplierPayments => Set<SupplierPayment>();
    public DbSet<SupplierPaymentCorrection> SupplierPaymentCorrections => Set<SupplierPaymentCorrection>();
    public DbSet<SupplierPaymentVersion> SupplierPaymentVersions => Set<SupplierPaymentVersion>();

    private void ConfigureSupplierPayments(ModelBuilder builder)
    {
        var payment = builder.Entity<SupplierPayment>();
        payment.ToTable("SupplierPayments", "Purchasing", t => t.HasCheckConstraint("CK_SupplierPayments_Details",
            "[Amount]>0 AND [PaymentDate]<=[PostingDate] AND [EffectiveDate]<=[PostingDate] AND [Method] COLLATE Latin1_General_100_BIN2 IN ('Bank','Cash') AND ISJSON([EvidenceJson],OBJECT)=1 AND DATALENGTH([EvidenceJson])<=262144"));
        payment.HasKey(x => new { x.TenantId, x.Id });
        payment.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        payment.HasIndex(x => new { x.TenantId, x.Id, x.RevisionId }).IsUnique();
        payment.HasIndex(x => new { x.TenantId, x.SupplierId, x.PurchaseOrderId, x.PostingDate });
        payment.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false);
        payment.Property(x => x.Method).HasMaxLength(12).IsUnicode(false);
        payment.Property(x => x.Amount).HasPrecision(28, 4);
        payment.Property(x => x.FundingAccountPurpose).HasMaxLength(40);
        payment.Property(x => x.Reference).HasMaxLength(200);
        payment.Property(x => x.Notes).HasMaxLength(2000);
        payment.HasOne<Supplier>().WithMany().HasForeignKey(x => new { x.TenantId, x.SupplierId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        payment.HasOne<DraftOrder>().WithMany().HasForeignKey(x => new { x.TenantId, x.PurchaseOrderId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        payment.HasOne<AccountingAccount>().WithMany().HasForeignKey(x => new { x.TenantId, x.FundingAccountId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        payment.HasOne<SupplierFinancialGroup>().WithMany().HasForeignKey(x => new { x.TenantId, x.GroupId }).OnDelete(DeleteBehavior.Restrict);
        payment.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var correction = builder.Entity<SupplierPaymentCorrection>();
        correction.ToTable("SupplierPaymentCorrections", "Purchasing", t => t.HasCheckConstraint("CK_SupplierPaymentCorrections_Reason",
            "DATALENGTH([Reason])>0 AND DATALENGTH([Reason])<=4000"));
        correction.HasKey(x => new { x.TenantId, x.Id });
        correction.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        correction.HasIndex(x => new { x.TenantId, x.OriginalPaymentId }).IsUnique();
        correction.Property(x => x.Reason).HasMaxLength(2000);
        correction.HasOne<SupplierPayment>().WithMany().HasForeignKey(x => new { x.TenantId, x.OriginalPaymentId }).OnDelete(DeleteBehavior.Restrict);
        correction.HasOne<SupplierPayment>().WithMany().HasForeignKey(x => new { x.TenantId, x.ReplacementPaymentId }).OnDelete(DeleteBehavior.Restrict);
        correction.HasOne<SupplierFinancialGroup>().WithMany().HasForeignKey(x => new { x.TenantId, x.GroupId }).OnDelete(DeleteBehavior.Restrict);
        correction.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var version = builder.Entity<SupplierPaymentVersion>();
        version.ToTable("SupplierPaymentVersions", "Purchasing");
        version.HasKey(x => new { x.TenantId, x.PaymentId });
        version.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        version.Property(x => x.RowVersion).IsRowVersion();
        version.HasOne<SupplierPayment>().WithMany().HasForeignKey(x => new { x.TenantId, x.PaymentId }).OnDelete(DeleteBehavior.Restrict);
    }
}
