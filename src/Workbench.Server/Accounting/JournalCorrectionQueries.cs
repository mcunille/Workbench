// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore;
using Workbench.Server.Persistence;

namespace Workbench.Server.Accounting;

public sealed record JournalCorrectionEvidence(Guid CorrectionId, string Role,
    Guid OriginalJournalId, Guid ReversalJournalId, Guid? ReplacementJournalId,
    string Reason, DateOnly PostingDate, DateTimeOffset RecordedAtUtc,
    string SnapshotJson, string SnapshotSha256);

internal static class JournalCorrectionQueries
{
    internal static async Task<IReadOnlyList<JournalCorrectionEvidence>> ForJournal(
        Guid journalId, WorkbenchDbContext database, CancellationToken ct)
    {
        var groups = await database.JournalCorrectionGroups.AsNoTracking()
            .Where(group => group.OriginalJournalId == journalId ||
                group.ReversalJournalId == journalId || group.ReplacementJournalId == journalId)
            .OrderBy(group => group.RecordedAtUtc).ThenBy(group => group.Id)
            .ToListAsync(ct);
        return groups.Select(group => new JournalCorrectionEvidence(group.Id,
            group.OriginalJournalId == journalId ? "Original" :
                group.ReversalJournalId == journalId ? "Reversal" : "Replacement",
            group.OriginalJournalId, group.ReversalJournalId, group.ReplacementJournalId,
            group.Reason, group.PostingDate, group.RecordedAtUtc,
            group.EvidenceJson, Convert.ToHexString(group.EvidenceSha256))).ToArray();
    }
}
