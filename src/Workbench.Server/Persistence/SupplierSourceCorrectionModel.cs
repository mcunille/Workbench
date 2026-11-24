// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Accounting;
using Workbench.Server.Identity;
using Workbench.Server.Purchasing;

namespace Workbench.Server.Persistence;

public partial class WorkbenchDbContext
{
    public DbSet<SupplierSourceCorrection> SupplierSourceCorrections => Set<SupplierSourceCorrection>();
    public DbSet<SupplierSourceCorrectionMember> SupplierSourceCorrectionMembers => Set<SupplierSourceCorrectionMember>();
    public DbSet<SupplierCashSettlementRelease> SupplierCashSettlementReleases => Set<SupplierCashSettlementRelease>();

    private void ConfigureSupplierSourceCorrections(ModelBuilder builder)
    {
        var correction = builder.Entity<SupplierSourceCorrection>();
        correction.ToTable("SupplierSourceCorrections", "Purchasing", t => t.HasCheckConstraint("CK_SupplierSourceCorrections_Owner",
            "LEN([Reason])>0 AND (([SourceKind] COLLATE Latin1_General_100_BIN2='SupplierCredit' AND [OriginalCreditId] IS NOT NULL AND [OriginalCreditId]=[SourceId] AND [OriginalBillId] IS NULL AND [OriginalCashSourceId] IS NULL) OR ([SourceKind] COLLATE Latin1_General_100_BIN2='SupplierBill' AND [OriginalBillId] IS NOT NULL AND [OriginalBillId]=[SourceId] AND [OriginalCreditId] IS NULL AND [OriginalCashSourceId] IS NULL) OR ([SourceKind] COLLATE Latin1_General_100_BIN2 IN ('SupplierRefund','SupplierRefundRepayment') AND [OriginalCashSourceId] IS NOT NULL AND [OriginalCashSourceId]=[SourceId] AND [OriginalCreditId] IS NULL AND [OriginalBillId] IS NULL)) AND (([ReplacementSourceId] IS NULL AND [ReplacementSourceRevisionId] IS NULL) OR ([ReplacementSourceId] IS NOT NULL AND [ReplacementSourceRevisionId] IS NOT NULL))"));
        correction.HasKey(x => new { x.TenantId, x.Id });
        correction.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        correction.HasIndex(x => new { x.TenantId, x.SourceKind, x.SourceId }).IsUnique();
        // Successor intent is reserved before child creation. The source owner binds its
        // actual row and immutable members before finalizing the shared financial receipt.
        correction.Property(x => x.SourceKind).HasMaxLength(32).IsUnicode(false);
        correction.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false);
        correction.Property(x => x.Reason).HasMaxLength(2000);
        correction.Property(x => x.OriginalSourceVersion).HasMaxLength(8).IsFixedLength();
        correction.HasOne<SupplierCredit>().WithMany().HasForeignKey(x => new { x.TenantId, x.OriginalCreditId }).OnDelete(DeleteBehavior.Restrict);
        correction.HasOne<SupplierBill>().WithMany().HasForeignKey(x => new { x.TenantId, x.OriginalBillId }).OnDelete(DeleteBehavior.Restrict);
        correction.HasOne<SupplierCashSource>().WithMany().HasForeignKey(x => new { x.TenantId, x.OriginalCashSourceId }).OnDelete(DeleteBehavior.Restrict);
        correction.HasOne<Supplier>().WithMany().HasForeignKey(x => new { x.TenantId, x.SupplierId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        correction.HasOne<DraftOrder>().WithMany().HasForeignKey(x => new { x.TenantId, x.PurchaseOrderId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        correction.HasOne<SupplierFinancialGroup>().WithMany().HasForeignKey(x => new { x.TenantId, x.GroupId }).OnDelete(DeleteBehavior.Restrict);
        correction.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var member = builder.Entity<SupplierSourceCorrectionMember>();
        member.ToTable("SupplierSourceCorrectionMembers", "Purchasing", t => t.HasCheckConstraint("CK_SupplierSourceCorrectionMembers_Effects",
            "[Kind] COLLATE Latin1_General_100_BIN2 IN ('ReverseSource','ReverseApplication','ReleaseSettlement','Replacement','Application','RecognitionAdjustment') AND ISJSON([EffectsJson],OBJECT)=1 AND DATALENGTH([EffectsJson])<=262144"));
        member.HasKey(x => new { x.TenantId, x.Id });
        member.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        member.Property(x => x.Kind).HasMaxLength(40).IsUnicode(false);
        member.HasIndex(x => new { x.TenantId, x.CorrectionId, x.Kind, x.EntityId }).IsUnique();
        member.HasOne<SupplierSourceCorrection>().WithMany().HasForeignKey(x => new { x.TenantId, x.CorrectionId }).OnDelete(DeleteBehavior.Restrict);
        member.HasOne<SupplierFinancialGroup>().WithMany().HasForeignKey(x => new { x.TenantId, x.GroupId }).OnDelete(DeleteBehavior.Restrict);
        member.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.TenantId, x.JournalId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        member.HasOne<RecognitionSideEvent>().WithMany().HasForeignKey(x => new { x.TenantId, x.RecognitionEventId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        member.HasOne<SupplierOpenItem>().WithMany().HasForeignKey(x => new { x.TenantId, x.ItemId }).OnDelete(DeleteBehavior.Restrict);

        var release = builder.Entity<SupplierCashSettlementRelease>();
        release.ToTable("SupplierCashSettlementReleases", "Purchasing", t => t.HasCheckConstraint("CK_SupplierCashSettlementReleases_Amount", "[Amount]>0"));
        release.HasKey(x => new { x.TenantId, x.SettlementId });
        release.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        release.Property(x => x.Amount).HasPrecision(28, 4);
        release.HasOne<SupplierCashSettlement>().WithMany().HasForeignKey(x => new { x.TenantId, x.SettlementId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        release.HasOne<SupplierSourceCorrection>().WithMany().HasForeignKey(x => new { x.TenantId, x.CorrectionId }).OnDelete(DeleteBehavior.Restrict);
        release.HasOne<SupplierFinancialGroup>().WithMany().HasForeignKey(x => new { x.TenantId, x.GroupId }).OnDelete(DeleteBehavior.Restrict);
        release.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.TenantId, x.JournalId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        release.HasOne<SupplierItemMovement>().WithMany().HasForeignKey(x => new { x.TenantId, x.RestoredMovementId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        release.HasOne<SupplierItemMovement>().WithMany().HasForeignKey(x => new { x.TenantId, x.OpeningMovementId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        release.HasOne<AccountingAccount>().WithMany().HasForeignKey(x => new { x.TenantId, x.ClearingAccountId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        release.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
