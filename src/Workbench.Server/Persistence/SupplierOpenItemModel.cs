// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Accounting;
using Workbench.Server.Identity;
using Workbench.Server.Purchasing;

namespace Workbench.Server.Persistence;

public partial class WorkbenchDbContext
{
    public DbSet<SupplierOpenItem> SupplierOpenItems => Set<SupplierOpenItem>();
    public DbSet<SupplierItemMovement> SupplierItemMovements => Set<SupplierItemMovement>();
    public DbSet<SupplierApplication> SupplierApplications => Set<SupplierApplication>();
    public DbSet<SupplierApplicationReversal> SupplierApplicationReversals => Set<SupplierApplicationReversal>();
    public DbSet<SupplierControlAttribution> SupplierControlAttributions => Set<SupplierControlAttribution>();
    public DbSet<SupplierFinancialGroup> SupplierFinancialGroups => Set<SupplierFinancialGroup>();
    public DbSet<SupplierFinancialReceipt> SupplierFinancialReceipts => Set<SupplierFinancialReceipt>();
    public DbSet<SupplierItemVersion> SupplierItemVersions => Set<SupplierItemVersion>();
    public DbSet<SupplierApplicationVersion> SupplierApplicationVersions => Set<SupplierApplicationVersion>();

    private void ConfigureSupplierOpenItems(ModelBuilder builder)
    {
        var item = builder.Entity<SupplierOpenItem>();
        item.ToTable("SupplierOpenItems", "Purchasing", t => t.HasCheckConstraint("CK_SupplierOpenItems_Kind",
            "[Kind] COLLATE Latin1_General_100_BIN2 IN ('Payable','Advance','CreditReceivable','RefundClearing') AND ISJSON([SourceSnapshotJson],OBJECT)=1 AND DATALENGTH([SourceSnapshotJson])<=262144"));
        item.HasKey(x => new { x.TenantId, x.Id });
        item.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        item.Property(x => x.Kind).HasMaxLength(24).IsUnicode(false);
        item.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false);
        item.Property(x => x.SourceKind).HasMaxLength(40).IsUnicode(false);
        item.HasIndex(x => new { x.TenantId, x.SourceKind, x.SourceId, x.SourceRevisionId, x.Kind }).IsUnique();
        item.HasIndex(x => new { x.TenantId, x.SupplierId, x.PurchaseOrderId, x.Kind });
        item.HasOne<Supplier>().WithMany().HasForeignKey(x => new { x.TenantId, x.SupplierId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        item.HasOne<DraftOrder>().WithMany().HasForeignKey(x => new { x.TenantId, x.PurchaseOrderId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        item.HasOne<SupplierBill>().WithMany().HasForeignKey(x => new { x.TenantId, x.BillId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var group = builder.Entity<SupplierFinancialGroup>();
        group.ToTable("SupplierFinancialGroups", "Purchasing", t => t.HasCheckConstraint("CK_SupplierFinancialGroups_Operation",
            "[Operation] COLLATE Latin1_General_100_BIN2 IN ('OpenRecognitionPayable','RecordPayment','Apply','ReverseApplication','CorrectPayment','CorrectSource')"));
        group.HasKey(x => new { x.TenantId, x.Id });
        group.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        group.Property(x => x.Operation).HasMaxLength(40).IsUnicode(false);
        group.HasIndex(x => new { x.TenantId, x.SourceId, x.Operation }).IsUnique();

        var movement = builder.Entity<SupplierItemMovement>();
        movement.ToTable("SupplierItemMovements", "Purchasing", t => t.HasCheckConstraint("CK_SupplierItemMovements_Amount",
            "[Amount]<>0 AND [EventKind] COLLATE Latin1_General_100_BIN2 IN ('Open','Apply','ReverseApplication','ReverseSource')"));
        movement.HasKey(x => new { x.TenantId, x.Id });
        movement.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        movement.Property(x => x.EventKind).HasMaxLength(24).IsUnicode(false);
        movement.Property(x => x.Amount).HasPrecision(28, 4);
        movement.HasIndex(x => new { x.TenantId, x.ItemId, x.PostingDate, x.Id });
        movement.HasIndex(x => new { x.TenantId, x.RecognitionEventId, x.EventKind })
            .IsUnique().HasFilter("[RecognitionEventId] IS NOT NULL");
        movement.HasOne<SupplierOpenItem>().WithMany().HasForeignKey(x => new { x.TenantId, x.ItemId }).OnDelete(DeleteBehavior.Restrict);
        movement.HasOne<SupplierFinancialGroup>().WithMany().HasForeignKey(x => new { x.TenantId, x.GroupId }).OnDelete(DeleteBehavior.Restrict);
        movement.HasOne<JournalSourceEvent>().WithMany().HasForeignKey(x => new { x.TenantId, x.SourceEventId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        movement.HasOne<RecognitionSideEvent>().WithMany().HasForeignKey(x => new { x.TenantId, x.RecognitionEventId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var application = builder.Entity<SupplierApplication>();
        application.ToTable("SupplierApplications", "Purchasing", t => t.HasCheckConstraint("CK_SupplierApplications_Amount", "[Amount]>0 AND [FundingItemId]<>[DebtItemId]"));
        application.HasKey(x => new { x.TenantId, x.Id });
        application.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        application.Property(x => x.Amount).HasPrecision(28, 4);
        application.HasOne<SupplierFinancialGroup>().WithMany().HasForeignKey(x => new { x.TenantId, x.GroupId }).OnDelete(DeleteBehavior.Restrict);
        application.HasOne<SupplierOpenItem>().WithMany().HasForeignKey(x => new { x.TenantId, x.FundingItemId }).OnDelete(DeleteBehavior.Restrict);
        application.HasOne<SupplierOpenItem>().WithMany().HasForeignKey(x => new { x.TenantId, x.DebtItemId }).OnDelete(DeleteBehavior.Restrict);
        application.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var reversal = builder.Entity<SupplierApplicationReversal>();
        reversal.ToTable("SupplierApplicationReversals", "Purchasing", t => t.HasCheckConstraint("CK_SupplierApplicationReversals_Reason",
            "DATALENGTH([Reason])>0 AND DATALENGTH([Reason])<=4000"));
        reversal.HasKey(x => new { x.TenantId, x.Id });
        reversal.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        reversal.Property(x => x.Reason).HasMaxLength(2000);
        reversal.HasIndex(x => new { x.TenantId, x.ApplicationId }).IsUnique();
        reversal.HasOne<SupplierApplication>().WithMany().HasForeignKey(x => new { x.TenantId, x.ApplicationId }).OnDelete(DeleteBehavior.Restrict);
        reversal.HasOne<SupplierFinancialGroup>().WithMany().HasForeignKey(x => new { x.TenantId, x.GroupId }).OnDelete(DeleteBehavior.Restrict);
        reversal.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var attribution = builder.Entity<SupplierControlAttribution>();
        attribution.ToTable("SupplierControlAttributions", "Purchasing", t => t.HasCheckConstraint("CK_SupplierControlAttributions_Amount", "[Amount]<>0"));
        attribution.HasKey(x => new { x.TenantId, x.Id });
        attribution.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        attribution.Property(x => x.AccountPurpose).HasMaxLength(40);
        attribution.Property(x => x.Amount).HasPrecision(28, 4);
        attribution.HasIndex(x => new { x.TenantId, x.JournalId, x.Ordinal, x.MovementId }).IsUnique();
        attribution.HasOne<SupplierFinancialGroup>().WithMany().HasForeignKey(x => new { x.TenantId, x.GroupId }).OnDelete(DeleteBehavior.Restrict);
        attribution.HasOne<SupplierItemMovement>().WithMany().HasForeignKey(x => new { x.TenantId, x.MovementId }).OnDelete(DeleteBehavior.Restrict);
        attribution.HasOne<JournalLineRow>().WithMany().HasForeignKey(x => new { x.TenantId, x.JournalId, x.Ordinal }).OnDelete(DeleteBehavior.Restrict);
        attribution.HasOne<AccountingAccount>().WithMany().HasForeignKey(x => new { x.TenantId, x.AccountId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var receipt = builder.Entity<SupplierFinancialReceipt>();
        receipt.ToTable("SupplierFinancialReceipts", "Purchasing", t => t.HasCheckConstraint("CK_SupplierFinancialReceipts_Input",
            "[RequestId]<>'00000000-0000-0000-0000-000000000000' AND ISJSON([CanonicalInput],OBJECT)=1 AND DATALENGTH([CanonicalInput])<=262144 AND DATALENGTH([InputSha256])=32 AND ISJSON([ResultJson],OBJECT)=1"));
        receipt.HasKey(x => new { x.TenantId, x.RequestId });
        receipt.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        receipt.Property(x => x.Operation).HasMaxLength(40).IsUnicode(false);
        receipt.Property(x => x.InputSha256).HasMaxLength(32).IsFixedLength();
        receipt.HasOne<SupplierFinancialGroup>().WithMany().HasForeignKey(x => new { x.TenantId, x.GroupId }).OnDelete(DeleteBehavior.Restrict);
        receipt.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var itemVersion = builder.Entity<SupplierItemVersion>();
        itemVersion.ToTable("SupplierItemVersions", "Purchasing");
        itemVersion.HasKey(x => new { x.TenantId, x.ItemId });
        itemVersion.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        itemVersion.Property(x => x.RowVersion).IsRowVersion();
        itemVersion.HasOne<SupplierOpenItem>().WithMany().HasForeignKey(x => new { x.TenantId, x.ItemId }).OnDelete(DeleteBehavior.Restrict);

        var applicationVersion = builder.Entity<SupplierApplicationVersion>();
        applicationVersion.ToTable("SupplierApplicationVersions", "Purchasing");
        applicationVersion.HasKey(x => new { x.TenantId, x.ApplicationId });
        applicationVersion.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        applicationVersion.Property(x => x.RowVersion).IsRowVersion();
        applicationVersion.HasOne<SupplierApplication>().WithMany().HasForeignKey(x => new { x.TenantId, x.ApplicationId }).OnDelete(DeleteBehavior.Restrict);
    }
}
