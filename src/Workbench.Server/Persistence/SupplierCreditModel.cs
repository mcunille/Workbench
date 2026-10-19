// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Accounting;
using Workbench.Server.Identity;
using Workbench.Server.Purchasing;

namespace Workbench.Server.Persistence;

public partial class WorkbenchDbContext
{
    public DbSet<SupplierCredit> SupplierCredits => Set<SupplierCredit>();
    public DbSet<SupplierCreditClaim> SupplierCreditClaims => Set<SupplierCreditClaim>();
    public DbSet<SupplierCreditVersion> SupplierCreditVersions => Set<SupplierCreditVersion>();

    private void ConfigureSupplierCredits(ModelBuilder builder)
    {
        var credit = builder.Entity<SupplierCredit>();
        credit.ToTable("SupplierCredits", "Purchasing", t => t.HasCheckConstraint("CK_SupplierCredits_Source",
            "[Total]>0 AND [DocumentDate]<=[PostingDate] AND [EffectiveDate]<=[PostingDate] AND LEN([NormalizedReference])>0 AND LEN([Reason])>0 AND ISJSON([SourceSnapshotJson],OBJECT)=1 AND DATALENGTH([SourceSnapshotJson])<=262144"));
        credit.HasKey(x => new { x.TenantId, x.Id });
        credit.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        credit.HasIndex(x => new { x.TenantId, x.RevisionId }).IsUnique();
        credit.HasIndex(x => new { x.TenantId, x.SupplierId, x.NormalizedReference });
        credit.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false);
        credit.Property(x => x.Total).HasPrecision(28, 4);
        credit.Property(x => x.Reference).HasMaxLength(200);
        credit.Property(x => x.NormalizedReference).HasMaxLength(200);
        credit.Property(x => x.Reason).HasMaxLength(2000);
        credit.HasOne<Supplier>().WithMany().HasForeignKey(x => new { x.TenantId, x.SupplierId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        credit.HasOne<DraftOrder>().WithMany().HasForeignKey(x => new { x.TenantId, x.PurchaseOrderId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        credit.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        credit.HasOne<SupplierFinancialGroup>().WithMany().HasForeignKey(x => new { x.TenantId, x.GroupId })
            .OnDelete(DeleteBehavior.Restrict);

        var claim = builder.Entity<SupplierCreditClaim>();
        claim.ToTable("SupplierCreditClaims", "Purchasing", t => t.HasCheckConstraint("CK_SupplierCreditClaims_Amount",
            "[Amount]>0 AND [Treatment] COLLATE Latin1_General_100_BIN2 IN ('Expense','Prepayment','Inventory','RecoverableTax') AND (([OriginalJournalId] IS NOT NULL AND [OriginalJournalOrdinal] IS NOT NULL AND [OriginalJournalOrdinal] BETWEEN 1 AND 1000) OR ([OriginalJournalId] IS NULL AND [OriginalJournalOrdinal] IS NULL AND [Treatment]='Expense'))"));
        claim.HasKey(x => new { x.TenantId, x.Id });
        claim.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        claim.Property(x => x.Amount).HasPrecision(28, 4);
        claim.Property(x => x.ComponentKey).HasMaxLength(200);
        claim.Property(x => x.Treatment).HasMaxLength(24).IsUnicode(false);
        claim.HasIndex(x => new { x.TenantId, x.CreditId, x.RecognitionEventId, x.ComponentKey }).IsUnique();
        claim.HasOne<SupplierCredit>().WithMany().HasForeignKey(x => new { x.TenantId, x.CreditId }).OnDelete(DeleteBehavior.Restrict);
        claim.HasOne<SupplierBillRevision>().WithMany().HasForeignKey(x => new { x.TenantId, x.BillId, x.BillRevisionId })
            .HasPrincipalKey(x => new { x.TenantId, x.BillId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        claim.HasOne<RecognitionComponent>().WithMany().HasForeignKey(x => new { x.TenantId, x.RecognitionEventId, x.ComponentKey })
            .HasPrincipalKey(x => new { x.TenantId, x.EventId, x.ComponentKey }).OnDelete(DeleteBehavior.Restrict);
        claim.HasOne<JournalLineRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.OriginalJournalId, x.OriginalJournalOrdinal })
            .HasPrincipalKey(x => new { x.TenantId, x.JournalId, x.Ordinal }).OnDelete(DeleteBehavior.Restrict);
        claim.HasOne<AccountingAccount>().WithMany().HasForeignKey(x => new { x.TenantId, x.AccountId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var version = builder.Entity<SupplierCreditVersion>();
        version.ToTable("SupplierCreditVersions", "Purchasing");
        version.HasKey(x => new { x.TenantId, x.CreditId });
        version.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        version.Property(x => x.RowVersion).IsRowVersion();
        version.HasOne<SupplierCredit>().WithMany().HasForeignKey(x => new { x.TenantId, x.CreditId }).OnDelete(DeleteBehavior.Restrict);
    }
}
