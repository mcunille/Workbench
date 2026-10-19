// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Accounting;
using Workbench.Server.Identity;
using Workbench.Server.Purchasing;
using Workbench.Server.Tenancy;

namespace Workbench.Server.Persistence;

public partial class WorkbenchDbContext
{
    public DbSet<RecognitionUnit> RecognitionUnits => Set<RecognitionUnit>();
    public DbSet<RecognitionSideEvent> RecognitionSideEvents => Set<RecognitionSideEvent>();
    public DbSet<RecognitionComponent> RecognitionComponents => Set<RecognitionComponent>();
    public DbSet<RecognitionMatch> RecognitionMatches => Set<RecognitionMatch>();
    public DbSet<RecognitionCorrectionGroup> RecognitionCorrectionGroups => Set<RecognitionCorrectionGroup>();
    public DbSet<RecognitionEventCorrection> RecognitionEventCorrections => Set<RecognitionEventCorrection>();
    public DbSet<RecognitionGroupReceipt> RecognitionGroupReceipts => Set<RecognitionGroupReceipt>();

    private void ConfigurePurchaseRecognition(ModelBuilder builder)
    {
        var unit = builder.Entity<RecognitionUnit>();
        unit.ToTable("RecognitionUnits", "Purchasing", table => table.HasCheckConstraint("CK_RecognitionUnits_Identity",
            "[PurchaseOrderRevision]>0 AND [PolicyVersion]>0 AND [Quantity]>0 AND [Classification] IN ('Expense','Inventory') AND DATALENGTH([GoodsReference])>0 AND DATALENGTH([QuantityUnit])>0"));
        unit.HasKey(x => x.Id);
        unit.HasAlternateKey(x => new { x.TenantId, x.Id });
        unit.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        unit.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false);
        unit.Property(x => x.GoodsReference).HasMaxLength(200);
        unit.Property(x => x.Classification).HasMaxLength(16).IsUnicode(false);
        unit.Property(x => x.Quantity).HasPrecision(28, 6);
        unit.Property(x => x.QuantityUnit).HasMaxLength(40);
        unit.HasIndex(x => new { x.TenantId, x.PurchaseOrderId, x.Id });
        unit.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        unit.HasOne<DraftOrder>().WithMany().HasForeignKey(x => new { x.TenantId, x.PurchaseOrderId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        unit.HasOne<Supplier>().WithMany().HasForeignKey(x => new { x.TenantId, x.SupplierId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var group = builder.Entity<RecognitionCorrectionGroup>();
        group.ToTable("RecognitionCorrectionGroups", "Purchasing", table => table.HasCheckConstraint("CK_RecognitionCorrectionGroups_Operation",
            "[Operation] IN ('Reverse','Replace') AND DATALENGTH([Reason])>0 AND DATALENGTH([Reason])<=4000"));
        group.HasKey(x => x.Id);
        group.HasAlternateKey(x => new { x.TenantId, x.Id });
        group.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        group.Property(x => x.Operation).HasMaxLength(16).IsUnicode(false);
        group.Property(x => x.Reason).HasMaxLength(2000);
        group.HasOne<RecognitionUnit>().WithMany().HasForeignKey(x => new { x.TenantId, x.UnitId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        group.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        group.HasOne<RecognitionUnit>().WithMany().HasForeignKey(x => new { x.TenantId, x.ReplacementUnitId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        group.HasOne<RecognitionMatch>().WithMany().HasForeignKey(x => new { x.TenantId, x.OriginalMatchId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        group.HasOne<RecognitionMatch>().WithMany().HasForeignKey(x => new { x.TenantId, x.ReplacementMatchId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        group.HasIndex(x => new { x.TenantId, x.UnitId }).IsUnique();

        var correction = builder.Entity<RecognitionEventCorrection>();
        correction.ToTable("RecognitionEventCorrections", "Purchasing");
        correction.HasKey(x => new { x.TenantId, x.OriginalEventId });
        correction.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        correction.HasOne<RecognitionSideEvent>().WithMany().HasForeignKey(x => new { x.TenantId, x.OriginalEventId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        correction.HasOne<RecognitionSideEvent>().WithMany().HasForeignKey(x => new { x.TenantId, x.ReplacementEventId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        correction.HasOne<RecognitionCorrectionGroup>().WithMany().HasForeignKey(x => new { x.TenantId, x.CorrectionGroupId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        correction.HasOne<JournalCorrectionGroup>().WithMany().HasForeignKey(x => new { x.TenantId, x.AccountingCorrectionGroupId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var side = builder.Entity<RecognitionSideEvent>();
        side.ToTable("RecognitionSideEvents", "Purchasing", table => table.HasCheckConstraint("CK_RecognitionSideEvents_Evidence",
            "[Side] IN ('Recognition','Invoice') AND [EventRevision]>0 AND [SourceQuantity]>0 AND [SourceAmount]>=0 AND ISJSON([EvidenceJson],OBJECT)=1 AND DATALENGTH([EvidenceJson])<=262144 AND DATALENGTH([EvidenceSha256])=32"));
        side.HasKey(x => x.Id);
        side.HasAlternateKey(x => new { x.TenantId, x.Id });
        side.HasAlternateKey(x => new { x.TenantId, x.UnitId, x.Id });
        side.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        side.Property(x => x.Side).HasMaxLength(16).IsUnicode(false);
        side.Property(x => x.EvidenceMutationPermission).HasMaxLength(100);
        side.Property(x => x.SourceComponentKey).HasMaxLength(200);
        side.Property(x => x.SubdivisionKey).HasMaxLength(200);
        side.Property(x => x.SourceQuantity).HasPrecision(28, 6);
        side.Property(x => x.SourceAmount).HasPrecision(28, 4);
        side.Property(x => x.EvidenceSha256).HasMaxLength(32).IsFixedLength();
        side.HasIndex(x => new { x.TenantId, x.UnitId, x.Side, x.EventRevision }).IsUnique();
        side.HasIndex(x => new { x.TenantId, x.Side, x.SourceId, x.SourceRevision, x.SourceComponentKey, x.SubdivisionKey }).IsUnique();
        side.HasIndex(x => new { x.TenantId, x.PostingDate, x.Id });
        side.HasOne<RecognitionUnit>().WithMany().HasForeignKey(x => new { x.TenantId, x.UnitId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        side.HasOne<RecognitionCorrectionGroup>().WithMany().HasForeignKey(x => new { x.TenantId, x.CorrectionGroupId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        side.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        side.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.TenantId, x.JournalId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var component = builder.Entity<RecognitionComponent>();
        component.ToTable("RecognitionComponents", "Purchasing", table => table.HasCheckConstraint("CK_RecognitionComponents_Kind",
            "[Kind] IN ('BaseCost','Discount','Freight','Charge','NonrecoverableTax','RecoverableTax','Rounding') AND ([Amount]>=0 OR [Kind]='Rounding')"));
        component.HasKey(x => new { x.TenantId, x.EventId, x.ComponentKey });
        component.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        component.Property(x => x.ComponentKey).HasMaxLength(200);
        component.Property(x => x.Kind).HasMaxLength(32).IsUnicode(false);
        component.Property(x => x.Amount).HasPrecision(28, 4);
        component.Property(x => x.Reason).HasMaxLength(2000);
        component.Property(x => x.AssignedCostComponentKey).HasMaxLength(200);
        component.HasOne<RecognitionSideEvent>().WithMany().HasForeignKey(x => new { x.TenantId, x.EventId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var match = builder.Entity<RecognitionMatch>();
        match.ToTable("RecognitionMatches", "Purchasing");
        match.HasKey(x => x.Id);
        match.HasAlternateKey(x => new { x.TenantId, x.Id });
        match.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        match.HasIndex(x => new { x.TenantId, x.RecognitionEventId }).IsUnique();
        match.HasIndex(x => new { x.TenantId, x.InvoiceEventId }).IsUnique();
        match.HasOne<RecognitionUnit>().WithMany().HasForeignKey(x => new { x.TenantId, x.UnitId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        match.HasOne<RecognitionSideEvent>().WithMany().HasForeignKey(x => new { x.TenantId, x.UnitId, x.RecognitionEventId })
            .HasPrincipalKey(x => new { x.TenantId, x.UnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        match.HasOne<RecognitionSideEvent>().WithMany().HasForeignKey(x => new { x.TenantId, x.UnitId, x.InvoiceEventId })
            .HasPrincipalKey(x => new { x.TenantId, x.UnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        match.HasOne<RecognitionCorrectionGroup>().WithMany().HasForeignKey(x => new { x.TenantId, x.CorrectionGroupId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var receipt = builder.Entity<RecognitionGroupReceipt>();
        receipt.ToTable("RecognitionGroupReceipts", "Purchasing", table => table.HasCheckConstraint("CK_RecognitionGroupReceipts_Input",
            "[RequestId]<>'00000000-0000-0000-0000-000000000000' AND [CommandVersion]=1 AND [CommandKind] IN ('Post','Reverse','Replace') AND ISJSON([CanonicalInput],OBJECT)=1 AND DATALENGTH([CanonicalInput])<=262144 AND DATALENGTH([InputSha256])=32 AND ISJSON([ResultJson],OBJECT)=1 AND DATALENGTH([ResultJson])<=262144"));
        receipt.HasKey(x => new { x.TenantId, x.RequestId });
        receipt.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        receipt.Property(x => x.CommandKind).HasMaxLength(16).IsUnicode(false);
        receipt.Property(x => x.InputSha256).HasMaxLength(32).IsFixedLength();
        receipt.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
