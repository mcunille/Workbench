// Copyright (c) 2026 The White Stag Collection.
using System.Text.Json.Serialization;

namespace Workbench.Server.Accounting;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountingPolicies(string? Country, string? Region, string? Currency, int? Scale,
    int? FiscalStartMonth, string? StartApproach, DateOnly? PlannedStartDate, int? RetentionYears,
    string? RetentionRationale, string? FrameworkNotes);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountingMapping(string Slot, Guid AccountId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CoverageClass(string Label, string? SourceReference, string? PolicyReference,
    string? ReconciliationReference, string? PrerequisiteReference);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountingCoverage(Guid AccountId, bool Included, string? ExclusionRationale,
    string? EvidenceKind, DateOnly? FromDate, DateOnly? ToDate, string? EvidenceReference,
    string? Rationale, bool AttestedComplete, IReadOnlyList<CoverageClass> Classes);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountingConfiguration(AccountingPolicies Policies,
    IReadOnlyList<AccountingMapping> Mappings, IReadOnlyList<AccountingCoverage> Coverage);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveAccountingConfigurationRequest(Guid RequestId, string ExpectedVersion, AccountingConfiguration Configuration);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AccountingAccountContent(string Code, string Name, string Type, string Purpose, string? Description);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateAccountingAccountsRequest(Guid RequestId, IReadOnlyList<AccountingAccountContent> Accounts);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateAccountingAccountRequest(Guid RequestId, string ExpectedVersion, string Code, string Name, string? Description);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArchiveAccountingAccountRequest(Guid RequestId, string ExpectedVersion, [property: JsonRequired] bool IsArchived);
public sealed record AccountingAccountResponse(Guid Id, string Code, string Name, string Type, string Purpose,
    string? Description, bool IsArchived, string Version);
public sealed record AccountingAccountPage(IReadOnlyList<AccountingAccountResponse> Items, string? NextCursor);
public sealed record AccountingSaveResponse(Guid RequestId, string SavedVersion, IReadOnlyList<Guid> AccountIds);
public sealed record AccountingSetupResponse(AccountingConfiguration Configuration, string Version,
    bool SetupComplete, bool BookkeepingAvailable, IReadOnlyList<string> MissingItems,
    IReadOnlyList<string> Blockers);
public sealed record AccountingCountry(string Code, string Name, IReadOnlyList<AccountingOption> Regions);
public sealed record AccountingOption(string Code, string Name);
public sealed record AccountingCurrency(string Code, string Name, int Scale);
public sealed record AccountingCatalogResponse(string Version, IReadOnlyList<AccountingCountry> Countries,
    IReadOnlyList<AccountingCurrency> Currencies, IReadOnlyList<string> AccountTypes,
    IReadOnlyList<string> AccountPurposes, IReadOnlyList<string> MappingSlots,
    IReadOnlyList<AccountingAccountContent> StarterAccounts);
