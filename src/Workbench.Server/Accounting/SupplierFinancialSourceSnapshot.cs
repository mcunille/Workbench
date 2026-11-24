// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Persistence;
using Workbench.Server.Purchasing;

namespace Workbench.Server.Accounting;

internal static partial class SupplierReconciliationQueries
{
    internal sealed class CashJournalOwner
    {
        public Guid CashSourceId { get; set; }
        public Guid JournalId { get; set; }
        public Guid SourceEventId { get; set; }
    }

    internal sealed partial class Snapshot
    {
        private List<SupplierCredit> credits = [];
        private List<SupplierCashSource> cashSources = [];
        private List<SupplierCreditClaim> creditClaims = [];
        private List<CashJournalOwner> cashJournalOwners = [];
        private List<Guid> verifiedReleases = [];
        private List<SupplierCashSettlement> settlements = [];
        private List<SupplierCashSettlementRelease> releases = [];
        private List<SupplierSourceCorrection> sourceCorrections = [];
        private List<SupplierSourceCorrectionMember> sourceCorrectionMembers = [];

        internal ILookup<Guid, SupplierCredit> CreditsById { get; private set; } = null!;
        internal ILookup<Guid, SupplierCashSource> CashById { get; private set; } = null!;
        internal ILookup<Guid, SupplierCreditClaim> ClaimsByCredit { get; private set; } = null!;
        internal ILookup<Guid, CashJournalOwner> CashOwnersByEvent { get; private set; } = null!;
        internal ILookup<Guid, SupplierCashSettlement> SettlementsByCash { get; private set; } = null!;
        internal ILookup<Guid, SupplierCashSettlement> SettlementsByItem { get; private set; } = null!;
        internal ILookup<Guid, SupplierCashSettlement> SettlementsById { get; private set; } = null!;
        internal ILookup<Guid, SupplierCashSettlementRelease> ReleasesBySettlement { get; private set; } = null!;
        internal ILookup<Guid, SupplierSourceCorrection> SourceCorrectionsByGroup { get; private set; } = null!;
        internal ILookup<(string Kind, Guid Id, Guid Revision), SupplierSourceCorrection> CorrectionsByOriginal { get; private set; } = null!;
        internal ILookup<(string Kind, Guid? Id, Guid? Revision), SupplierSourceCorrection> CorrectionsByReplacement { get; private set; } = null!;
        private ILookup<Guid, SupplierSourceCorrectionMember> membersByCorrection { get; set; } = null!;
        private ILookup<Guid, JournalLineRow> linesByJournal { get; set; } = null!;
        private HashSet<Guid> releaseOwners { get; set; } = [];

        private void IndexFinancialSources()
        {
            CreditsById = Index(credits, row => row.Id);
            CashById = Index(cashSources, row => row.Id);
            ClaimsByCredit = Index(creditClaims, row => row.CreditId);
            CashOwnersByEvent = Index(cashJournalOwners, row => row.SourceEventId);
            SettlementsByCash = Index(settlements, row => row.CashSourceId);
            SettlementsByItem = Index(settlements, row => row.ItemId);
            SettlementsById = Index(settlements, row => row.Id);
            ReleasesBySettlement = Index(releases, row => row.SettlementId);
            SourceCorrectionsByGroup = Index(sourceCorrections, row => row.GroupId);
            CorrectionsByOriginal = Index(sourceCorrections, row => (row.SourceKind, row.SourceId, row.SourceRevisionId));
            CorrectionsByReplacement = Index(sourceCorrections, row => (row.SourceKind, row.ReplacementSourceId, row.ReplacementSourceRevisionId));
            membersByCorrection = Index(sourceCorrectionMembers, row => row.CorrectionId);
            linesByJournal = Index(Lines, row => row.JournalId);
            releaseOwners = Observe(verifiedReleases).ToHashSet();
        }

        private async Task LoadFinancialSources(WorkbenchDbContext db, IQueryable<SupplierFinancialGroup> groups,
            IQueryable<JournalEntry> journals, CancellationToken ct)
        {
            var creditQuery = db.SupplierCredits.AsNoTracking().Where(row => groups.Any(group => group.Id == row.GroupId) &&
                row.RecordedAtUtc <= Filter.RecordedThrough);
            credits = await creditQuery.Select(row => new SupplierCredit
            {
                Id = row.Id,
                RevisionId = row.RevisionId,
                GroupId = row.GroupId,
                RecordedAtUtc = row.RecordedAtUtc,
                DocumentDate = row.DocumentDate,
                EffectiveDate = row.EffectiveDate,
                PostingDate = row.PostingDate,
                Total = row.Total,
                Reference = row.Reference,
                Currency = row.Currency
            }).ToListAsync(ct);
            creditClaims = await db.SupplierCreditClaims.AsNoTracking()
                .Where(row => creditQuery.Any(credit => credit.Id == row.CreditId)).ToListAsync(ct);
            var cashQuery = db.SupplierCashSources.AsNoTracking().Where(row => groups.Any(group => group.Id == row.GroupId) &&
                row.RecordedAtUtc <= Filter.RecordedThrough);
            cashSources = await cashQuery.Select(row => new SupplierCashSource
            {
                Id = row.Id,
                RevisionId = row.RevisionId,
                GroupId = row.GroupId,
                RecordedAtUtc = row.RecordedAtUtc,
                Kind = row.Kind,
                Currency = row.Currency,
                ActualDate = row.ActualDate,
                EffectiveDate = row.EffectiveDate,
                PostingDate = row.PostingDate,
                Amount = row.Amount,
                Reference = row.Reference,
                Method = row.Method,
                FundingAccountId = row.FundingAccountId,
                FundingAccountVersion = row.FundingAccountVersion
            }).ToListAsync(ct);
            settlements = await db.SupplierCashSettlements.AsNoTracking()
                .Where(row => cashQuery.Any(cash => cash.Id == row.CashSourceId) && journals.Any(journal => journal.Id == row.JournalId))
                .ToListAsync(ct);
            releases = await db.SupplierCashSettlementReleases.AsNoTracking().Where(row =>
                groups.Any(group => group.Id == row.GroupId) && journals.Any(journal => journal.Id == row.JournalId) &&
                row.RecordedAtUtc <= Filter.RecordedThrough).ToListAsync(ct);
            cashJournalOwners = await db.Database.SqlQuery<CashJournalOwner>($"""
                SELECT owner.CashSourceId, owner.JournalId, owner.SourceEventId
                FROM Purchasing.SupplierCashSources cash
                JOIN Purchasing.SupplierFinancialGroups g ON g.TenantId=cash.TenantId AND g.Id=cash.GroupId
                CROSS APPLY Purchasing.SupplierCashControl({Filter.Tenant},cash.Id) owner
                JOIN Accounting.JournalEntries j ON j.TenantId=cash.TenantId AND j.Id=owner.JournalId
                WHERE cash.TenantId={Filter.Tenant} AND g.Sequence<={Filter.GroupCeiling} AND j.Sequence<={Filter.JournalCeiling}
                  AND g.RecordedAtUtc<={Filter.RecordedThrough} AND j.RecordedAtUtc<={Filter.RecordedThrough}
                """).ToListAsync(ct);
            verifiedReleases = await db.Database.SqlQuery<Guid>($"""
                SELECT released.SettlementId AS Value FROM Purchasing.SupplierCashSettlementReleases released
                JOIN Purchasing.SupplierFinancialGroups g ON g.TenantId=released.TenantId AND g.Id=released.GroupId
                JOIN Accounting.JournalEntries j ON j.TenantId=released.TenantId AND j.Id=released.JournalId
                CROSS APPLY Purchasing.SupplierRefundReleaseControl({Filter.Tenant},released.SettlementId) control
                WHERE released.TenantId={Filter.Tenant} AND g.Sequence<={Filter.GroupCeiling} AND j.Sequence<={Filter.JournalCeiling}
                  AND g.RecordedAtUtc<={Filter.RecordedThrough} AND j.RecordedAtUtc<={Filter.RecordedThrough}
                """).ToListAsync(ct);
            var corrections = db.SupplierSourceCorrections.AsNoTracking().Where(row =>
                groups.Any(group => group.Id == row.GroupId && group.RecordedAtUtc == row.RecordedAtUtc) &&
                row.RecordedAtUtc <= Filter.RecordedThrough);
            sourceCorrections = await corrections.ToListAsync(ct);
            var verifiedJournalMembers = await db.Database.SqlQuery<Guid>($"""
                SELECT DISTINCT member.Id AS Value
                FROM Purchasing.SupplierSourceCorrectionMembers member
                JOIN Purchasing.SupplierFinancialGroups g ON g.TenantId=member.TenantId AND g.Id=member.GroupId
                JOIN Purchasing.SupplierFinancialReceipts receipt ON receipt.TenantId=g.TenantId AND receipt.GroupId=g.Id
                  AND receipt.RecordedAtUtc=g.RecordedAtUtc AND receipt.Operation=CASE g.Operation
                    WHEN 'CorrectCredit' THEN 'CorrectSupplierCredit' WHEN 'CorrectBill' THEN 'CorrectSupplierBill'
                    WHEN 'CorrectRefund' THEN 'CorrectSupplierRefund' WHEN 'CorrectRepayment' THEN 'CorrectSupplierRefundRepayment' END
                  AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(receipt.ResultJson,'$.groupId'))=g.Id
                  AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(receipt.ResultJson,'$.sourceId'))=g.SourceId
                JOIN Purchasing.SupplierSourceCorrections correction ON correction.TenantId=member.TenantId AND correction.Id=member.CorrectionId
                  AND correction.GroupId=g.Id AND correction.ActorId=receipt.ActorId
                JOIN Accounting.JournalEntries j ON j.TenantId=member.TenantId AND j.Id=member.JournalId
                  AND j.RecordedAtUtc=g.RecordedAtUtc
                WHERE member.TenantId={Filter.Tenant} AND g.Sequence<={Filter.GroupCeiling} AND j.Sequence<={Filter.JournalCeiling}
                  AND g.RecordedAtUtc<={Filter.RecordedThrough}
                  AND EXISTS (SELECT 1 FROM OPENJSON(receipt.ResultJson,'$.journalIds') posted
                    WHERE TRY_CONVERT(uniqueidentifier,posted.value)=member.JournalId)
                """).ToListAsync(ct);
            var ownedMembers = verifiedJournalMembers.ToHashSet();
            sourceCorrectionMembers = await db.SupplierSourceCorrectionMembers.AsNoTracking().Where(row =>
                corrections.Any(correction => correction.Id == row.CorrectionId && correction.GroupId == row.GroupId) &&
                (row.JournalId == null || journals.Any(journal => journal.Id == row.JournalId)))
                .Select(row => new SupplierSourceCorrectionMember
                {
                    Id = row.Id,
                    CorrectionId = row.CorrectionId,
                    GroupId = row.GroupId,
                    Kind = row.Kind,
                    EntityId = row.EntityId,
                    JournalId = row.JournalId,
                    RecognitionEventId = row.RecognitionEventId,
                    ItemId = row.ItemId
                }).ToListAsync(ct);
            sourceCorrectionMembers.RemoveAll(row => row.JournalId.HasValue && !ownedMembers.Contains(row.Id));
        }
    }
}
