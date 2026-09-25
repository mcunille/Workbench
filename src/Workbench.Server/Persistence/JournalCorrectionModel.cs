// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore;
using Workbench.Server.Accounting;
using Workbench.Server.Identity;

namespace Workbench.Server.Persistence;

public partial class WorkbenchDbContext
{
    public DbSet<JournalCorrectionGroup> JournalCorrectionGroups => Set<JournalCorrectionGroup>();
    public DbSet<JournalCorrectionReceipt> JournalCorrectionReceipts => Set<JournalCorrectionReceipt>();

    private void ConfigureJournalCorrections(ModelBuilder builder)
    {
        var group = builder.Entity<JournalCorrectionGroup>();
        group.ToTable("CorrectionGroups", "Accounting", t =>
        {
            t.HasCheckConstraint("CK_CorrectionGroups_Evidence", "LEN(TRIM([Reason]))>0 AND DATALENGTH([Reason])<=4000 AND ISJSON([EvidenceJson],OBJECT)=1 AND DATALENGTH([EvidenceJson])<=262144 AND DATALENGTH([EvidenceSha256])=32");
            t.HasCheckConstraint("CK_CorrectionGroups_Roles", "[OriginalJournalId]<>[ReversalJournalId] AND (([ReplacementJournalId] IS NULL AND [ReplacementSourceEventId] IS NULL AND [ReplacementRequestId] IS NULL AND [ReplacementSourceRevision] IS NULL) OR ([ReplacementJournalId] IS NOT NULL AND [ReplacementSourceEventId] IS NOT NULL AND [ReplacementRequestId] IS NOT NULL AND [ReplacementSourceRevision] IS NOT NULL AND [ReplacementJournalId]<>[OriginalJournalId] AND [ReplacementJournalId]<>[ReversalJournalId]))");
        });
        group.HasKey(x => new { x.TenantId, x.Id });
        group.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        group.Property(x => x.Reason).HasMaxLength(2000);
        group.Property(x => x.EvidenceSha256).HasMaxLength(32).IsFixedLength();
        foreach (var role in new[] { "Original", "Reversal", "Replacement" })
        {
            group.HasIndex("TenantId", role + "JournalId").IsUnique();
            group.HasOne<JournalEntry>().WithMany().HasForeignKey("TenantId", role + "JournalId")
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            group.HasOne<JournalSourceEvent>().WithMany().HasForeignKey("TenantId", role + "SourceEventId")
                .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        }
        foreach (var role in new[] { "Reversal", "Replacement" })
            group.HasOne<JournalPostingReceipt>().WithMany().HasForeignKey("TenantId", role + "RequestId")
                .OnDelete(DeleteBehavior.Restrict);
        group.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var receipt = builder.Entity<JournalCorrectionReceipt>();
        receipt.ToTable("CorrectionReceipts", "Accounting", t => t.HasCheckConstraint("CK_CorrectionReceipts_Input",
            "[RequestId]<>'00000000-0000-0000-0000-000000000000' AND [SourceCommandVersion]>0 AND ISJSON([CanonicalInput],OBJECT)=1 AND DATALENGTH([CanonicalInput])<=262144 AND DATALENGTH([InputSha256])=32"));
        receipt.HasKey(x => new { x.TenantId, x.RequestId });
        receipt.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        receipt.Property(x => x.SourceCommandKind).HasMaxLength(64);
        receipt.Property(x => x.InputSha256).HasMaxLength(32).IsFixedLength();
        receipt.HasOne<JournalCorrectionGroup>().WithMany().HasForeignKey(x => new { x.TenantId, x.CorrectionId })
            .OnDelete(DeleteBehavior.Restrict);
        receipt.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
