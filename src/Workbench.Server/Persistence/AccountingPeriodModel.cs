// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore;
using Workbench.Server.Accounting;
using Workbench.Server.Identity;
using Workbench.Server.Tenancy;

namespace Workbench.Server.Persistence;

public partial class WorkbenchDbContext
{
    public DbSet<AccountingPeriod> AccountingPeriods => Set<AccountingPeriod>();
    public DbSet<AccountingPeriodClosure> AccountingPeriodClosures => Set<AccountingPeriodClosure>();
    public DbSet<AccountingPeriodCloseReceipt> AccountingPeriodCloseReceipts => Set<AccountingPeriodCloseReceipt>();

    private void ConfigureAccountingPeriods(ModelBuilder builder)
    {
        var period = builder.Entity<AccountingPeriod>();
        period.ToTable("Periods", "Accounting", t => t.HasCheckConstraint("CK_Periods_Calendar",
            "DAY([PeriodStart])=1 AND [PeriodEnd]=EOMONTH([PeriodStart]) AND DAY([FiscalYearStart])=1 AND MONTH([FiscalYearStart])=[FiscalStartMonth] AND YEAR([FiscalYearStart])=YEAR([PeriodStart])-CASE WHEN MONTH([PeriodStart])<[FiscalStartMonth] THEN 1 ELSE 0 END AND [AccountingStartDate]<=[PeriodEnd] AND [Scale] BETWEEN 0 AND 4"));
        period.HasKey(x => new { x.TenantId, x.PeriodStart });
        period.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        period.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false);
        period.Property(x => x.StartApproach).HasMaxLength(40);
        period.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);

        var closure = builder.Entity<AccountingPeriodClosure>();
        closure.ToTable("PeriodClosures", "Accounting", t => t.HasCheckConstraint("CK_PeriodClosures_Evidence",
            "LEN(TRIM(NCHAR(9)+NCHAR(10)+NCHAR(11)+NCHAR(12)+NCHAR(13)+NCHAR(32)+NCHAR(133)+NCHAR(160)+NCHAR(5760)+NCHAR(8192)+NCHAR(8193)+NCHAR(8194)+NCHAR(8195)+NCHAR(8196)+NCHAR(8197)+NCHAR(8198)+NCHAR(8199)+NCHAR(8200)+NCHAR(8201)+NCHAR(8202)+NCHAR(8232)+NCHAR(8233)+NCHAR(8239)+NCHAR(8287)+NCHAR(12288) FROM [Reason]))>0 AND DATALENGTH([Reason])<=4000 AND ISJSON([EvidenceJson],OBJECT)=1 AND DATALENGTH([EvidenceJson])<=262144 AND DATALENGTH([EvidenceSha256])=32"));
        closure.HasKey(x => new { x.TenantId, x.PeriodStart });
        closure.HasAlternateKey(x => new { x.TenantId, x.Id });
        closure.HasAlternateKey(x => new { x.TenantId, x.PeriodStart, x.Id });
        closure.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        closure.Property(x => x.Reason).HasMaxLength(2000);
        closure.Property(x => x.EvidenceSha256).HasMaxLength(32).IsFixedLength();
        closure.HasOne<AccountingPeriod>().WithMany().HasForeignKey(x => new { x.TenantId, x.PeriodStart })
            .OnDelete(DeleteBehavior.Restrict);
        closure.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        var receipt = builder.Entity<AccountingPeriodCloseReceipt>();
        receipt.ToTable("PeriodCloseReceipts", "Accounting", t => t.HasCheckConstraint("CK_PeriodCloseReceipts_Input",
            "[RequestId]<>'00000000-0000-0000-0000-000000000000' AND [CommandKind]='Period.Close' AND [CommandVersion]=1 AND ISJSON([CanonicalInput],OBJECT)=1 AND DATALENGTH([CanonicalInput])<=262144 AND DATALENGTH([InputSha256])=32"));
        receipt.HasKey(x => new { x.TenantId, x.RequestId });
        receipt.HasQueryFilter(x => (Guid?)x.TenantId == TenantContext.TenantId);
        receipt.Property(x => x.CommandKind).HasMaxLength(64);
        receipt.Property(x => x.InputSha256).HasMaxLength(32).IsFixedLength();
        receipt.HasOne<AccountingPeriodClosure>().WithMany()
            .HasForeignKey(x => new { x.TenantId, x.PeriodStart, x.ClosureId })
            .HasPrincipalKey(x => new { x.TenantId, x.PeriodStart, x.Id }).OnDelete(DeleteBehavior.Restrict);
        receipt.HasOne<AccountingPeriod>().WithMany().HasForeignKey(x => new { x.TenantId, x.PeriodStart })
            .OnDelete(DeleteBehavior.Restrict);
        receipt.HasOne<WorkbenchUser>().WithMany().HasForeignKey(x => new { x.TenantId, x.ActorId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
