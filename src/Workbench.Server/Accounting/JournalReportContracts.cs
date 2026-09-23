// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Accounting;

public sealed record JournalSourceEvidence(Guid Id, string SourceKind, Guid SourceId, Guid SourceRevision,
    string EventKind, int RuleVersion, Guid ActorId, DateOnly DocumentDate, DateOnly EffectiveDate,
    DateOnly PostingDate, string? Reference, string? Reason, string SnapshotJson, string SnapshotSha256,
    DateTimeOffset RecordedAtUtc);

public sealed record JournalHeader(Guid Id, long Sequence, Guid SourceEventId, string SourceKind,
    Guid SourceId, Guid SourceRevision, string EventKind, Guid ConfigurationVersion, string Currency,
    int Scale, DateOnly DocumentDate, DateOnly EffectiveDate, DateOnly PostingDate, DateTimeOffset RecordedAtUtc,
    Guid ActorId, string? Reference, string? Reason, string DebitTotal, string CreditTotal);

public sealed record JournalLine(int Ordinal, Guid AccountId, Guid AccountVersion, string AccountCode,
    string AccountName, string AccountType, string AccountPurpose, string Debit, string Credit);

public sealed record JournalDetail(JournalHeader Header, IReadOnlyList<JournalLine> Lines, JournalSourceEvidence Source);

public sealed record ReportTotals(string Debit, string Credit);

public sealed record JournalPage(IReadOnlyList<JournalHeader> Items, string? NextCursor, DateOnly PostingThrough,
    DateTimeOffset RecordedThrough, ReportTotals WholeFilterTotals, ReportTotals PageTotals,
    string ActivityLabel = "Recorded journal activity");

public sealed record AccountJournalLine(Guid JournalId, long Sequence, Guid SourceEventId, int Ordinal,
    DateOnly PostingDate, DateTimeOffset RecordedAtUtc, string Currency, int Scale,
    string AccountCode, string AccountName, string AccountType, string AccountPurpose,
    string Debit, string Credit);

public sealed record AccountJournalPage(Guid AccountId, IReadOnlyList<AccountJournalLine> Items,
    string? NextCursor, DateOnly PostingThrough, DateTimeOffset RecordedThrough,
    ReportTotals WholeFilterTotals, ReportTotals PageTotals,
    string ActivityLabel = "Recorded journal activity");

public sealed record TrialBalanceAccount(Guid AccountId, string Code, string Name, bool IsArchived,
    string DebitActivity, string CreditActivity, string DebitMinusCredit);

public sealed record TrialBalancePage(IReadOnlyList<TrialBalanceAccount> Items, string? NextCursor,
    DateOnly PostingThrough, DateTimeOffset RecordedThrough, string? Currency, int Scale,
    ReportTotals WholeFilterTotals, ReportTotals PageTotals,
    string ActivityLabel = "Recorded journal activity");
