// Copyright (c) 2026 The White Stag Collection.
using Workbench.Server.Purchasing;

namespace Workbench.Server.Accounting;

internal static partial class SupplierReconciliationQueries
{
    internal sealed partial class Snapshot
    {
        internal bool HasVerifiedRelease(Guid id) => releaseOwners.Contains(id);

        internal bool HasVerifiedCash(SupplierCashSource cash) =>
            Evidence(SettlementsByCash, cash.Id).Any(row => Journals.TryGetValue(row.JournalId, out var journal) &&
                Sources.TryGetValue(journal.SourceEventId, out var source) && source.SourceKind == cash.Kind &&
                source.SourceRevision == cash.RevisionId && CashOwner(source)?.JournalId == journal.Id);

        internal bool HasVerifiedCredit(Guid id) =>
            Evidence(controlsByItem, id).Count(row => row.AccountPurpose == "SupplierCreditReceivable") == 1;

        internal bool HasVerifiedSourceEvent(JournalSourceEvent source) => source.SourceKind switch
        {
            "SupplierCredit" => source.EventKind == "Credit" && HasVerifiedCredit(source.SourceId) &&
                Evidence(MovementsByItem, source.SourceId).Any(row => row.EventKind == "Open" && row.SourceEventId == source.Id),
            "SupplierRefund" or "SupplierRefundRepayment" => CashOwner(source) is not null,
            "SupplierRefundRelease" => source.EventKind == "Release" && HasVerifiedRelease(source.SourceId) &&
                Evidence(ReleasesBySettlement, source.SourceId).Any(row => row.CorrectionId == source.SourceRevision &&
                    Journals.TryGetValue(row.JournalId, out var journal) && journal.SourceEventId == source.Id),
            _ => false
        };

        internal bool VisibleSourceCorrection(SupplierSourceCorrection correction) =>
            correction.PostingDate <= Filter.PostingThrough &&
            Evidence(membersByCorrection, correction.Id).Any(member => member.Kind == "ReverseSource" &&
                member.JournalId.HasValue && Journals.TryGetValue(member.JournalId.Value, out var journal) &&
                journal.PostingDate == correction.PostingDate && journal.RecordedAtUtc == correction.RecordedAtUtc &&
                Visible(journal));

        internal CashJournalOwner? CashOwner(JournalSourceEvent source)
        {
            var owners = Evidence(CashOwnersByEvent, source.Id).Take(2).ToArray();
            return owners.Length == 1 && owners[0].CashSourceId == source.SourceId ? owners[0] : null;
        }

        private bool OwnsCashSettlement(SupplierItemMovement movement, JournalSourceEvent source, SupplierControlAttribution[] attrs) =>
            movement.EventKind == "CashSettlement" && attrs.Length == 1 && attrs[0].Amount == movement.Amount &&
            CashOwner(source) is { } owner && owner.JournalId == attrs[0].JournalId &&
            Evidence(SettlementsByCash, owner.CashSourceId).Count(row => row.MovementId == movement.Id &&
                row.ItemId == movement.ItemId && row.JournalId == owner.JournalId && row.JournalOrdinal == attrs[0].Ordinal &&
                row.Amount == -movement.Amount) == 1;

        private bool OwnsReleasedCredit(SupplierItemMovement movement, JournalSourceEvent source, SupplierControlAttribution[] attrs) =>
            source.EventKind == "Release" && movement.EventKind == "ReleaseSettlement" && attrs.Length == 1 &&
            attrs[0].Ordinal == 1 && attrs[0].Amount == movement.Amount && releaseOwners.Contains(source.SourceId) &&
            Evidence(ReleasesBySettlement, source.SourceId).Count(row => row.RestoredMovementId == movement.Id &&
                row.GroupId == movement.GroupId && row.CorrectionId == source.SourceRevision &&
                row.JournalId == attrs[0].JournalId && row.Amount == movement.Amount &&
                Evidence(SettlementsById, row.SettlementId).Any(settlement => settlement.ItemId == movement.ItemId)) == 1;

        private bool OwnsBillSuccessor(SupplierItemMovement movement, SupplierOpenItem item, JournalSourceEvent source)
        {
            if (item.BillId is null || item.SourceKind != "SupplierBill" || source.EventKind != "Invoice") return false;
            return Evidence(SourceCorrectionsByGroup, movement.GroupId).Any(correction =>
                correction.SourceKind == "SupplierBill" && correction.ReplacementSourceId == item.Id &&
                correction.ReplacementSourceRevisionId == item.SourceRevisionId &&
                Evidence(membersByCorrection, correction.Id).Count(member => member.Kind == "Replacement" &&
                    member.EntityId == movement.RecognitionEventId && member.RecognitionEventId == movement.RecognitionEventId &&
                    member.ItemId == item.Id && member.JournalId.HasValue &&
                    Journals.TryGetValue(member.JournalId.Value, out var journal) && journal.SourceEventId == source.Id) == 1);
        }

        private bool OwnsSourceInverse(SupplierItemMovement movement, JournalCorrectionGroup inverse, JournalSourceEvent original)
        {
            if (movement.EventKind != "ReverseSource" || inverse.PostingDate != movement.PostingDate ||
                inverse.RecordedAtUtc != movement.RecordedAtUtc ||
                !Journals.TryGetValue(inverse.OriginalJournalId, out var originalJournal) || originalJournal.SourceEventId != original.Id)
                return false;
            var owners = Evidence(SourceCorrectionsByGroup, movement.GroupId).Where(correction =>
                correction.ActorId == inverse.ActorId && correction.PostingDate == inverse.PostingDate &&
                Evidence(membersByCorrection, correction.Id).Count(member => member.Kind == "ReverseSource" &&
                    member.JournalId == inverse.ReversalJournalId && BindsOriginal(member, correction, movement, inverse, original)) == 1);
            return owners.Take(2).Count() == 1 && ExactJournalInverse(inverse);
        }

        private bool BindsOriginal(SupplierSourceCorrectionMember member, SupplierSourceCorrection correction,
            SupplierItemMovement movement, JournalCorrectionGroup inverse, JournalSourceEvent original) =>
            original.SourceKind switch
            {
                "SupplierCredit" => correction.SourceKind == original.SourceKind && correction.OriginalCreditId == original.SourceId &&
                    correction.SourceId == original.SourceId && correction.SourceRevisionId == original.SourceRevision &&
                    member.EntityId == original.SourceId && member.ItemId == movement.ItemId && movement.ItemId == original.SourceId,
                "SupplierRefund" or "SupplierRefundRepayment" => correction.SourceKind == original.SourceKind &&
                    correction.OriginalCashSourceId == original.SourceId && correction.SourceId == original.SourceId &&
                    correction.SourceRevisionId == original.SourceRevision && member.EntityId == original.SourceId &&
                    CashOwner(original)?.JournalId == inverse.OriginalJournalId,
                "SupplierRefundRelease" => member.EntityId == inverse.OriginalJournalId && releaseOwners.Contains(original.SourceId) &&
                    Evidence(ReleasesBySettlement, original.SourceId).Any(release => release.JournalId == inverse.OriginalJournalId &&
                        release.CorrectionId == original.SourceRevision &&
                        Evidence(SettlementsById, release.SettlementId).Any(settlement =>
                            settlement.CashSourceId == correction.SourceId && correction.OriginalCashSourceId == settlement.CashSourceId &&
                            correction.SourceKind == "SupplierRefund")),
                "PurchaseRecognition" => original.EventKind == "Invoice" && member.EntityId == original.SourceId &&
                    member.RecognitionEventId == original.SourceId && movement.RecognitionEventId == original.SourceId &&
                    correction.SourceKind == "SupplierBill" && correction.OriginalBillId == movement.ItemId &&
                    correction.SourceId == movement.ItemId && correction.SourceRevisionId == original.SourceRevision &&
                    member.ItemId == movement.ItemId,
                _ => false
            };

        private bool ExactJournalInverse(JournalCorrectionGroup inverse)
        {
            var original = Evidence(linesByJournal, inverse.OriginalJournalId).OrderBy(line => line.Ordinal).ToArray();
            var reversed = Evidence(linesByJournal, inverse.ReversalJournalId).OrderBy(line => line.Ordinal).ToArray();
            return original.Length > 0 && original.Length == reversed.Length &&
                Observe(original.Zip(reversed)).All(pair =>
                {
                    var (old, next) = pair;
                    return old.Ordinal == next.Ordinal && old.AccountId == next.AccountId && old.AccountVersion == next.AccountVersion &&
                        old.AccountCode == next.AccountCode && old.AccountName == next.AccountName &&
                        old.AccountType == next.AccountType && old.AccountPurpose == next.AccountPurpose &&
                        old.Debit == next.Credit && old.Credit == next.Debit;
                });
        }
    }
}
