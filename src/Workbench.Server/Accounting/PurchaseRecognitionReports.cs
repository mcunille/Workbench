// Copyright (c) 2026 The White Stag Collection.
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Persistence;

namespace Workbench.Server.Accounting;

public sealed record PurchaseRecognitionComponent(string ComponentKey, string Kind, string Amount,
    string? Reason, string? AssignedCostComponentKey);

public sealed record PurchaseRecognitionCorrectionRelationship(Guid GroupId, string Role,
    Guid OriginalUnitId, Guid? ReplacementUnitId, Guid? OriginalMatchId, Guid? ReplacementMatchId,
    Guid OriginalEventId, Guid? ReplacementEventId, Guid? OriginalJournalId,
    Guid? ReversalJournalId, Guid? ReplacementJournalId);

public sealed record PurchaseRecognitionEvidence(Guid UnitId, Guid EventId, string Side, string JournalRole,
    int EventRevision, string Classification, string GoodsReference, string Quantity, string QuantityUnit,
    Guid SourceId, Guid SourceRevision, string SourceComponentKey, string SubdivisionKey,
    string SourceQuantity, string SourceAmount, string EvidenceJson, string EvidenceSha256,
    IReadOnlyList<PurchaseRecognitionComponent> Components, IReadOnlyList<Guid> MatchIds,
    IReadOnlyList<PurchaseRecognitionCorrectionRelationship> CorrectionGroups);

internal static class PurchaseRecognitionReports
{
    internal static async Task<PurchaseRecognitionEvidence?> ReadAsync(WorkbenchDbContext db,
        Guid journalId, CancellationToken cancellationToken)
    {
        var tenant = db.TenantContext.RequireTenantId();
        var side = await db.RecognitionSideEvents.AsNoTracking()
            .SingleOrDefaultAsync(e => e.TenantId == tenant && e.JournalId == journalId, cancellationToken);
        var journalRole = "Posted";
        if (side is null)
        {
            var originalJournalId = await db.JournalCorrectionGroups.AsNoTracking()
                .Where(group => group.TenantId == tenant && group.ReversalJournalId == journalId)
                .Select(group => (Guid?)group.OriginalJournalId)
                .SingleOrDefaultAsync(cancellationToken);
            if (originalJournalId is null) return null;
            side = await db.RecognitionSideEvents.AsNoTracking()
                .SingleOrDefaultAsync(e => e.TenantId == tenant && e.JournalId == originalJournalId,
                    cancellationToken);
            if (side is null) return null;
            journalRole = "Reversal";
        }

        var unit = await db.RecognitionUnits.AsNoTracking()
            .SingleAsync(u => u.TenantId == tenant && u.Id == side.UnitId, cancellationToken);
        var scale = await db.JournalEntries.AsNoTracking()
            .Where(j => j.TenantId == tenant && j.Id == journalId)
            .Select(j => j.Scale).SingleAsync(cancellationToken);
        var components = await db.RecognitionComponents.AsNoTracking()
            .Where(c => c.TenantId == tenant && c.EventId == side.Id)
            .OrderBy(c => c.ComponentKey).ToListAsync(cancellationToken);
        var matches = await db.RecognitionMatches.AsNoTracking()
            .Where(m => m.TenantId == tenant &&
                (m.RecognitionEventId == side.Id || m.InvoiceEventId == side.Id))
            .OrderBy(m => m.RecordedAtUtc).ThenBy(m => m.Id)
            .Select(m => m.Id).ToListAsync(cancellationToken);
        var corrections = await db.RecognitionEventCorrections.AsNoTracking()
            .Where(c => c.TenantId == tenant &&
                (c.OriginalEventId == side.Id || c.ReplacementEventId == side.Id))
            .ToListAsync(cancellationToken);
        var relationships = new List<PurchaseRecognitionCorrectionRelationship>(corrections.Count);
        foreach (var correction in corrections)
        {
            var group = await db.RecognitionCorrectionGroups.AsNoTracking()
                .SingleAsync(g => g.TenantId == tenant && g.Id == correction.CorrectionGroupId,
                    cancellationToken);
            var original = correction.OriginalEventId == side.Id ? side :
                await db.RecognitionSideEvents.AsNoTracking().SingleAsync(e => e.TenantId == tenant &&
                    e.Id == correction.OriginalEventId, cancellationToken);
            var replacement = correction.ReplacementEventId is Guid replacementId
                ? correction.ReplacementEventId == side.Id ? side :
                    await db.RecognitionSideEvents.AsNoTracking().SingleAsync(e => e.TenantId == tenant &&
                        e.Id == replacementId, cancellationToken)
                : null;
            var reversalJournalId = correction.AccountingCorrectionGroupId is Guid accountingId
                ? await db.JournalCorrectionGroups.AsNoTracking()
                    .Where(g => g.TenantId == tenant && g.Id == accountingId)
                    .Select(g => (Guid?)g.ReversalJournalId).SingleAsync(cancellationToken)
                : null;
            relationships.Add(new(group.Id,
                correction.OriginalEventId == side.Id ? "Original" : "Replacement",
                group.UnitId, group.ReplacementUnitId, group.OriginalMatchId, group.ReplacementMatchId,
                correction.OriginalEventId, correction.ReplacementEventId, original.JournalId,
                reversalJournalId, replacement?.JournalId));
        }
        relationships.Sort((a, b) => a.GroupId.CompareTo(b.GroupId));

        return new PurchaseRecognitionEvidence(unit.Id, side.Id, side.Side, journalRole,
            side.EventRevision, unit.Classification, unit.GoodsReference,
            Quantity(unit.Quantity), unit.QuantityUnit, side.SourceId, side.SourceRevision,
            side.SourceComponentKey, side.SubdivisionKey, Quantity(side.SourceQuantity),
            Money(side.SourceAmount, scale), side.EvidenceJson, Convert.ToHexString(side.EvidenceSha256),
            components.Select(c => new PurchaseRecognitionComponent(c.ComponentKey, c.Kind,
                Money(c.Amount, scale), c.Reason, c.AssignedCostComponentKey)).ToArray(),
            matches, relationships);
    }

    private static string Quantity(decimal value) => value.ToString("F6", CultureInfo.InvariantCulture);
    private static string Money(decimal value, int scale) => value.ToString($"F{scale}", CultureInfo.InvariantCulture);
}
