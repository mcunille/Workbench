// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Accounting;
using Workbench.Server.Identity;
using Workbench.Server.Purchasing;
using Workbench.Server.Storage;

namespace Workbench.Server.Persistence;

public partial class WorkbenchDbContext
{
    public DbSet<FinancialEvidenceSet> FinancialEvidenceSets => Set<FinancialEvidenceSet>();
    public DbSet<FinancialEvidenceLink> FinancialEvidenceLinks => Set<FinancialEvidenceLink>();
    public DbSet<FinancialEvidenceAddition> FinancialEvidenceAdditions => Set<FinancialEvidenceAddition>();
    public DbSet<FinancialEvidenceReceipt> FinancialEvidenceReceipts => Set<FinancialEvidenceReceipt>();
    public DbSet<FinancialEvidenceAttachmentState> FinancialEvidenceAttachmentStates => Set<FinancialEvidenceAttachmentState>();

    private void ConfigureFinancialEvidence(ModelBuilder builder)
    {
        var set = builder.Entity<FinancialEvidenceSet>();
        set.ToTable("FinancialEvidenceSets", "Accounting", t => t.HasCheckConstraint("CK_FinancialEvidenceSets_Owner", "[OwnerKind] COLLATE Latin1_General_100_BIN2 IN ('SupplierBill','SupplierPayment','PurchaseRecognition')"));
        set.HasKey(x => new { x.TenantId, x.Id });
        set.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        set.Property(x => x.OwnerKind).HasMaxLength(32).IsUnicode(false);
        set.Property(x => x.MissingEvidenceReason).HasMaxLength(2000);
        set.Property(x => x.MutationPermission).HasMaxLength(100);
        set.Property(x => x.SourceSnapshotSha256).HasMaxLength(32).IsFixedLength();
        set.Property(x => x.RowVersion).IsRowVersion();
        set.HasIndex(x => new { x.TenantId, x.OwnerKind, x.OwnerId, x.OwnerRevisionId }).IsUnique();
        set.HasOne<DraftOrder>().WithMany().HasForeignKey(x => new { x.TenantId, x.PurchaseOrderId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        set.HasOne<Supplier>().WithMany().HasForeignKey(x => new { x.TenantId, x.SupplierId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        set.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        set.HasOne<FinancialEvidenceSet>().WithMany().HasForeignKey(x => new { x.TenantId, x.InheritedEvidenceSetId }).OnDelete(DeleteBehavior.Restrict);

        var link = builder.Entity<FinancialEvidenceLink>();
        link.ToTable("FinancialEvidenceLinks", "Accounting", t => t.HasCheckConstraint("CK_FinancialEvidenceLinks_Policy", "[Length]>0 AND LEN([Sha256])=64 AND [Sha256] COLLATE Latin1_General_100_BIN2 NOT LIKE '%[^0-9A-F]%' AND (([RetentionYears] IS NULL AND [MinimumRetentionDeadlineUtc] IS NULL) OR ([RetentionYears] BETWEEN 1 AND 1000 AND [MinimumRetentionDeadlineUtc] IS NOT NULL AND [MinimumRetentionDeadlineUtc]>=[AnchorAtUtc]))"));
        link.HasKey(x => new { x.TenantId, x.Id });
        link.HasAlternateKey(x => new { x.TenantId, x.EvidenceSetId, x.Id });
        link.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        link.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        link.Property(x => x.Label).HasMaxLength(200);
        link.Property(x => x.MediaType).HasMaxLength(100);
        link.Property(x => x.Extension).HasMaxLength(10);
        link.Property(x => x.RetentionRationale).HasMaxLength(2000);
        link.HasIndex(x => new { x.TenantId, x.EvidenceSetId, x.DocumentId, x.RevisionId }).IsUnique();
        link.HasOne<FinancialEvidenceSet>().WithMany().HasForeignKey(x => new { x.TenantId, x.EvidenceSetId }).OnDelete(DeleteBehavior.Restrict);
        link.HasOne<PurchaseOrderDocument>().WithMany().HasForeignKey(x => new { x.TenantId, x.DocumentId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        link.HasOne<AttachmentRevision>().WithMany().HasForeignKey(x => new { x.TenantId, x.AttachmentId, x.RevisionId }).HasPrincipalKey(x => new { x.TenantId, x.AttachmentId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var receipt = builder.Entity<FinancialEvidenceReceipt>();
        receipt.ToTable("FinancialEvidenceReceipts", "Accounting", t => t.HasCheckConstraint("CK_FinancialEvidenceReceipts_Input", "ISJSON([CanonicalInput],OBJECT)=1 AND DATALENGTH([CanonicalInput])<=262144 AND ISJSON([ResultJson],OBJECT)=1"));
        receipt.HasKey(x => new { x.TenantId, x.RequestId });
        receipt.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        receipt.Property(x => x.Operation).HasMaxLength(24).IsUnicode(false);
        receipt.Property(x => x.InputSha256).HasMaxLength(32).IsFixedLength();
        receipt.HasOne<FinancialEvidenceSet>().WithMany().HasForeignKey(x => new { x.TenantId, x.EvidenceSetId }).OnDelete(DeleteBehavior.Restrict);
        receipt.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var addition = builder.Entity<FinancialEvidenceAddition>();
        addition.ToTable("FinancialEvidenceAdditions", "Accounting", t => t.HasCheckConstraint("CK_FinancialEvidenceAdditions_Reason", "LEN(TRIM([Reason]))>0 AND ([ReplacesLinkId] IS NULL OR [ReplacesLinkId]<>[LinkId])"));
        addition.HasKey(x => new { x.TenantId, x.Id });
        addition.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        addition.Property(x => x.Reason).HasMaxLength(2000);
        addition.HasIndex(x => new { x.TenantId, x.RequestId }).IsUnique();
        addition.HasOne<FinancialEvidenceLink>().WithMany().HasForeignKey(x => new { x.TenantId, x.EvidenceSetId, x.LinkId }).HasPrincipalKey(x => new { x.TenantId, x.EvidenceSetId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        addition.HasOne<FinancialEvidenceLink>().WithMany().HasForeignKey(x => new { x.TenantId, x.EvidenceSetId, x.ReplacesLinkId }).HasPrincipalKey(x => new { x.TenantId, x.EvidenceSetId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        addition.HasOne<FinancialEvidenceReceipt>().WithMany().HasForeignKey(x => new { x.TenantId, x.RequestId }).OnDelete(DeleteBehavior.Restrict);
        addition.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var state = builder.Entity<FinancialEvidenceAttachmentState>();
        state.ToTable("FinancialEvidenceAttachmentStates", "Storage");
        state.HasKey(x => new { x.TenantId, x.AttachmentId });
        state.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        state.Property(x => x.RowVersion).IsRowVersion();
        state.HasOne<Attachment>().WithMany().HasForeignKey(x => new { x.TenantId, x.AttachmentId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var disposal = builder.Entity<Accounting.FinancialEvidenceDisposal>();
        disposal.ToTable("FinancialEvidenceDisposals", "Accounting", t => t.HasCheckConstraint("CK_FinancialEvidenceDisposals_Grace", "[DeleteAfterUtc]>=DATEADD(day,7,[RemovedAtUtc])"));
        disposal.HasKey(x => new { x.TenantId, x.RequestId });
        disposal.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        disposal.HasIndex(x => new { x.TenantId, x.AttachmentId }).IsUnique();
        disposal.HasOne<FinancialEvidenceReceipt>().WithMany().HasForeignKey(x => new { x.TenantId, x.RequestId }).OnDelete(DeleteBehavior.Restrict);
        disposal.HasOne<Attachment>().WithMany().HasForeignKey(x => new { x.TenantId, x.AttachmentId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        disposal.HasOne<PurchaseOrderDocument>().WithMany().HasForeignKey(x => new { x.TenantId, x.DocumentId }).HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        var member = builder.Entity<FinancialEvidenceDisposalLink>();
        member.ToTable("FinancialEvidenceDisposalLinks", "Accounting");
        member.HasKey(x => new { x.TenantId, x.RequestId, x.LinkId });
        member.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        member.HasOne<Accounting.FinancialEvidenceDisposal>().WithMany().HasForeignKey(x => new { x.TenantId, x.RequestId }).OnDelete(DeleteBehavior.Restrict);
        member.HasOne<FinancialEvidenceLink>().WithMany().HasForeignKey(x => new { x.TenantId, Id = x.LinkId }).OnDelete(DeleteBehavior.Restrict);
    }
}
