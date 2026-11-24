// Copyright (c) 2026 The White Stag Collection.
using System.Data;
using System.Globalization;
using System.Numerics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Workbench.Server.Persistence;
using Workbench.Server.Purchasing;

namespace Workbench.Server.Accounting;

internal static partial class SupplierReconciliationQueries
{
    internal sealed partial class Snapshot
    {
        internal bool ValidSource(SupplierOpenItem i)
        {
            cancellation.ThrowIfCancellationRequested();
            if (validSources.TryGetValue(i.Id, out var valid)) return valid;
            var openings = Evidence(MovementsByItem, i.Id).Where(m => m.EventKind == "Open").ToArray();
            return validSources[i.Id] = Evidence(controlsByItem, i.Id).Count() == 1 && openings.Length > 0 && Observe(openings).All(m =>
                Sources.TryGetValue(m.SourceEventId, out var source) && (source.SourceKind == "SupplierPayment" && source.EventKind == "Payment" || OpeningSourceMatches(m, i, source)));
        }
        private bool ExactAttribution(SupplierControlAttribution a, SupplierItemMovement m, SupplierOpenItem item)
        {
            if (a.GroupId != m.GroupId || !Journals.TryGetValue(a.JournalId, out var journal) || journal.SourceEventId != m.SourceEventId ||
                journal.PostingDate != m.PostingDate || journal.Currency != item.Currency || journal.RecordedAtUtc != Groups[m.GroupId].RecordedAtUtc ||
                m.RecordedAtUtc != Groups[m.GroupId].RecordedAtUtc) return false;
            return Evidence(linesByOrdinal, (a.JournalId, a.Ordinal)).Any(l => l.AccountId == a.AccountId &&
                l.AccountVersion == a.AccountVersion && l.AccountPurpose == a.AccountPurpose && Family(l.AccountPurpose) == item.Kind) &&
                Evidence(controlsByItem, item.Id).Any(c => c.AccountId == a.AccountId && c.AccountVersion == a.AccountVersion && c.AccountPurpose == a.AccountPurpose);
        }

        // Prove complete source/item effects, not one-to-one gross payment movements.
        // Inverse effects retain exact historical line ownership through Accounting correction links.
        private bool ValidMovement(SupplierItemMovement m, SupplierOpenItem item)
        {
            cancellation.ThrowIfCancellationRequested();
            if (validMovements.TryGetValue(m.Id, out var valid)) return valid;
            return validMovements[m.Id] = ValidateMovement(m, item);
        }

        private bool ValidateMovement(SupplierItemMovement m, SupplierOpenItem item)
        {
            if (!ValidSource(item) || !Sources.TryGetValue(m.SourceEventId, out var source) ||
                source.PostingDate != m.PostingDate || source.RecordedAtUtc != Groups[m.GroupId].RecordedAtUtc || m.RecordedAtUtc != source.RecordedAtUtc) return false;
            var attrs = Evidence(attributionsByMovement, m.Id).ToArray();
            if (Observe(attrs).Any(a => !ExactAttribution(a, m, item)) || attrs.Length > 1) return false;
            if (source.SourceKind == "SupplierPayment" && source.EventKind == "Payment")
            {
                // SupplierPaymentControl validates all original payment movements and exact debt ordinals.
                return Evidence(controlsByItem, source.SourceId).Any() &&
                    (m.EventKind == "Open" && m.ItemId == source.SourceId || m.EventKind == "Apply" && Evidence(applicationsByGroup, m.GroupId).Any(a =>
                        a.GroupId == m.GroupId && a.FundingItemId == source.SourceId && (a.FundingItemId == m.ItemId || a.DebtItemId == m.ItemId) && -a.Amount == m.Amount));
            }
            var correction = Evidence(CorrectionsByReversalSource, m.SourceEventId).SingleOrDefault();
            if (correction is not null)
            {
                if (!Sources.TryGetValue(correction.OriginalSourceEventId, out var originalSource)) return false;
                var ownsPaymentInverse = originalSource.SourceKind == "SupplierPayment" && originalSource.EventKind == "Payment" && Evidence(PaymentCorrectionsByGroup, m.GroupId).Any(p => p.OriginalPaymentId == originalSource.SourceId);
                var ownsCompensation = Evidence(ReversalsById, originalSource.SourceId).Any(r => ReversalSourceMatches(r, originalSource) &&
                    Evidence(ApplicationsById, r.ApplicationId).Any(a => a.Id == r.ApplicationId && Evidence(PaymentCorrectionsByGroup, m.GroupId).Any(p => p.OriginalPaymentId == a.FundingItemId &&
                        Evidence(MovementsByItem, p.OriginalPaymentId).Any(open => open.EventKind == "Open" && open.GroupId == a.GroupId))));
                var ownerGroup = Groups[m.GroupId];
                var ownsRecognitionInverse = originalSource.SourceKind == "PurchaseRecognition" && originalSource.EventKind == "Invoice" && m.RecognitionEventId == originalSource.SourceId &&
                    ownerGroup.Operation == "CorrectSource" && Evidence(recognitionOwnersByGroup, m.GroupId).Any(o => o.CorrectionId == ownerGroup.SourceId) &&
                    Evidence(recognitionCorrectionsByGroup, ownerGroup.SourceId).Any(c => c.OriginalEventId == m.RecognitionEventId && c.AccountingCorrectionGroupId == correction.Id);
                if (!ownsPaymentInverse && !ownsCompensation && !ownsRecognitionInverse && !OwnsSourceInverse(m, correction, originalSource)) return false;
                var originals = Evidence(movementsBySourceItem, (correction.OriginalSourceEventId, m.ItemId)).Where(o => o.Amount == -m.Amount).ToArray();
                return Observe(originals).Any(o =>
                {
                    var originalAttrs = Evidence(attributionsByMovement, o.Id).ToArray();
                    return attrs.Length == originalAttrs.Length && Observe(attrs).All(a => Observe(originalAttrs).Any(old => old.JournalId == correction.OriginalJournalId &&
                        a.JournalId == correction.ReversalJournalId && old.Ordinal == a.Ordinal && old.AccountId == a.AccountId && old.AccountVersion == a.AccountVersion && old.Amount == -a.Amount));
                });
            }
            if (source.SourceKind is "SupplierRefund" or "SupplierRefundRepayment")
                return OwnsCashSettlement(m, source, attrs);
            if (source.SourceKind == "SupplierRefundRelease" && m.EventKind == "ReleaseSettlement")
                return OwnsReleasedCredit(m, source, attrs);
            if (m.EventKind == "Apply")
                return attrs.Length == 1 && attrs[0].Amount == m.Amount && Evidence(applicationsByGroup, m.GroupId).Any(a => a.GroupId == m.GroupId &&
                    OwnerSourceMatches(source, "SupplierApplication", "Apply", a.Id, a.GroupId, a.PostingDate, a.RecordedAtUtc) &&
                    -a.Amount == m.Amount && (a.FundingItemId == m.ItemId || a.DebtItemId == m.ItemId));
            if (m.EventKind == "ReverseApplication")
                return attrs.Length == 1 && attrs[0].Amount == m.Amount && Evidence(reversalsByGroup, m.GroupId).Any(r => ReversalSourceMatches(r, source) &&
                    Evidence(ApplicationsById, r.ApplicationId).Any(a => a.Id == r.ApplicationId && a.Amount == m.Amount && (a.FundingItemId == m.ItemId || a.DebtItemId == m.ItemId)));
            if (m.EventKind != "Open" || attrs.Length != 1 || attrs[0].Amount != m.Amount) return false;
            return OpeningSourceMatches(m, item, source);
        }

        private bool OwnerSourceMatches(JournalSourceEvent source, string kind, string eventKind, Guid id, Guid groupId,
            DateOnly postingDate, DateTimeOffset recordedAtUtc) =>
            source.SourceKind == kind && source.EventKind == eventKind && source.SourceId == id && source.SourceRevision == groupId &&
            source.PostingDate == postingDate && source.RecordedAtUtc == recordedAtUtc &&
            Groups.TryGetValue(groupId, out var group) && group.RecordedAtUtc == recordedAtUtc;

        private bool ReversalSourceMatches(SupplierApplicationReversal reversal, JournalSourceEvent source) =>
            OwnerSourceMatches(source, "SupplierApplicationReversal", "Reverse", reversal.Id, reversal.GroupId, reversal.PostingDate, reversal.RecordedAtUtc) &&
            Evidence(ApplicationsById, reversal.ApplicationId).Any(a => a.Id == reversal.ApplicationId && a.PostingDate <= reversal.PostingDate);

        private bool OpeningSourceMatches(SupplierItemMovement m, SupplierOpenItem item, JournalSourceEvent source)
        {
            if (source.SourceKind != "PurchaseRecognition") return item.SourceId == source.SourceId && item.SourceRevisionId == source.SourceRevision;
            var recognition = Evidence(recognitionById, m.RecognitionEventId).SingleOrDefault();
            var group = Groups[m.GroupId];
            var ownsOpening = Evidence(recognitionOwnersByGroup, group.Id).Any(o => o.EventId == m.RecognitionEventId &&
                (group.Operation == "OpenRecognitionPayable" && o.CorrectionId is null && (group.SourceId == item.BillId ||
                    Evidence(recognitionOwnersByGroup, group.Id).Any(member => member.EventId == group.SourceId)) ||
                 group.Operation == "CorrectSource" && o.CorrectionId == group.SourceId && Evidence(recognitionCorrectionsByGroup, group.SourceId).Any(c =>
                    c.CorrectionGroupId == group.SourceId && c.ReplacementEventId == m.RecognitionEventId)));
            if (!ownsOpening && !OwnsBillSuccessor(m, item, source)) return false;
            return recognition is not null && recognition.Side == "Invoice" && recognition.SourceAmount == m.Amount &&
                Journals.TryGetValue(recognition.JournalId ?? Guid.Empty, out var openingJournal) && openingJournal.SourceEventId == m.SourceEventId && source.SourceId == recognition.Id && source.SourceRevision == recognition.SourceRevision &&
                item.SourceRevisionId == recognition.SourceRevision && item.SourceId == (item.BillId ?? recognition.Id) &&
                (!item.BillId.HasValue || recognition.SourceId == item.BillId) && Evidence(recognitionUnitsById, recognition.UnitId).Any(u =>
                    u.SupplierId == item.SupplierId && u.PurchaseOrderId == item.PurchaseOrderId && u.Currency == item.Currency);
        }

    }
}
