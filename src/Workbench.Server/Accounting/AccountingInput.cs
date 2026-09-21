// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Accounting;

public static class AccountingInput
{
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public static AccountingAccountContent Normalize(AccountingAccountContent value) => value with
    { Code = (value.Code ?? "").Trim().ToUpperInvariant(), Name = (value.Name ?? "").Trim(), Description = Text(value.Description) };
    public static AccountingConfiguration Normalize(AccountingConfiguration value) => value with
    {
        Policies = value.Policies with
        {
            Country = Text(value.Policies.Country)?.ToUpperInvariant(),
            Region = Text(value.Policies.Region)?.ToUpperInvariant(),
            Currency = Text(value.Policies.Currency)?.ToUpperInvariant(),
            RetentionRationale = Text(value.Policies.RetentionRationale),
            FrameworkNotes = Text(value.Policies.FrameworkNotes)
        },
        Mappings = value.Mappings.OrderBy(m => m.Slot, StringComparer.Ordinal).ToArray(),
        Coverage = value.Coverage.OrderBy(c => c.AccountId).Select(c => c with
        {
            ExclusionRationale = Text(c.ExclusionRationale),
            EvidenceReference = Text(c.EvidenceReference),
            Rationale = Text(c.Rationale),
            Classes = c.Classes.Select(t => t with { Label = t.Label.Trim(), SourceReference = Text(t.SourceReference), PolicyReference = Text(t.PolicyReference), ReconciliationReference = Text(t.ReconciliationReference), PrerequisiteReference = Text(t.PrerequisiteReference) }).ToArray()
        }).ToArray()
    };
    public static Dictionary<string, string[]> ValidateAccounts(IReadOnlyList<AccountingAccountContent>? accounts)
    {
        var errors = new Dictionary<string, string[]>();
        if (accounts is null || accounts.Count is < 1 or > 100) { errors["accounts"] = ["Choose between 1 and 100 accounts."]; return errors; }
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < accounts.Count; i++)
        {
            var account = accounts[i]; var key = $"accounts[{i}]";
            if (account is null) { errors[key] = ["An account is required."]; continue; }
            var code = (account.Code ?? "").Trim();
            if (code.Length is < 1 or > 32 || code.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-')) errors[key + ".code"] = ["Use 1–32 letters, numbers, dots, underscores or hyphens."];
            if (!codes.Add(code)) errors[key + ".code"] = ["Account codes must be unique."];
            CheckText(errors, key + ".name", account.Name, 160, true);
            CheckText(errors, key + ".description", account.Description, 2000);
            if (!AccountingCatalog.Types.Contains(account.Type)) errors[key + ".type"] = ["Choose an account type."];
            if (!AccountingCatalog.Purposes.Contains(account.Purpose) || (AccountingCatalog.RequiredType(account.Purpose) is { } type && type != account.Type)) errors[key + ".purpose"] = ["Choose a purpose compatible with the account type."];
        }
        return errors;
    }
    public static Dictionary<string, string[]> Validate(AccountingConfiguration? configuration)
    {
        var errors = new Dictionary<string, string[]>();
        if (configuration?.Policies is not { } p || configuration.Mappings is null || configuration.Coverage is null)
        { errors["configuration"] = ["Policies, mappings and coverage are required."]; return errors; }
        var country = AccountingCatalog.Value.Countries.SingleOrDefault(c => c.Code == Text(p.Country)?.ToUpperInvariant());
        if (p.Country is not null && country is null) errors["policies.country"] = ["Choose a supported country."];
        if (p.Region is not null && (country is null || !country.Regions.Any(r => r.Code == Text(p.Region)?.ToUpperInvariant()))) errors["policies.region"] = ["Choose a region for this country."];
        if (p.Currency is not null && !AccountingCatalog.Value.Currencies.Any(c => c.Code == Text(p.Currency)?.ToUpperInvariant())) errors["policies.currency"] = ["Choose a supported currency."];
        if (p.Scale is < 0 or > 4) errors["policies.scale"] = ["Choose a scale from 0 to 4."];
        if (p.FiscalStartMonth is < 1 or > 12) errors["policies.fiscalStartMonth"] = ["Choose a fiscal start month."];
        if (p.StartApproach is not null && p.StartApproach is not "FromBeginning" and not "OpeningBalances") errors["policies.startApproach"] = ["Choose a start approach."];
        if (p.RetentionYears is < 1 or > 1000) errors["policies.retentionYears"] = ["Use 1–1000 years or leave unresolved."];
        CheckText(errors, "policies.retentionRationale", p.RetentionRationale, 2000);
        CheckText(errors, "policies.frameworkNotes", p.FrameworkNotes, 2000);
        if (configuration.Mappings.Count > AccountingCatalog.Slots.Length || configuration.Mappings.Any(m => m is null || !AccountingCatalog.Slots.Contains(m.Slot) || m.AccountId == Guid.Empty) || configuration.Mappings.Where(m => m is not null).Select(m => m.Slot).Distinct().Count() != configuration.Mappings.Count)
            errors["mappings"] = ["Use distinct supported slots with valid account identifiers."];
        if (configuration.Coverage.Count > 200 || configuration.Coverage.Any(c => c is null || c.AccountId == Guid.Empty) || configuration.Coverage.Where(c => c is not null).Select(c => c.AccountId).Distinct().Count() != configuration.Coverage.Count)
            errors["coverage"] = ["Use at most 200 distinct funding accounts."];
        for (var i = 0; i < configuration.Coverage.Count; i++)
        {
            var c = configuration.Coverage[i]; var key = $"coverage[{i}]"; if (c is null) continue;
            CheckText(errors, key + ".exclusionRationale", c.ExclusionRationale, 2000, !c.Included);
            CheckText(errors, key + ".rationale", c.Rationale, 2000, c.Included && c.EvidenceKind == "NoPriorActivity");
            CheckText(errors, key + ".evidenceReference", c.EvidenceReference, 500, c.Included && c.EvidenceKind == "Statement");
            if (c.Included && (c.EvidenceKind is not "Statement" and not "NoPriorActivity" || c.ToDate is null ||
                (c.EvidenceKind == "Statement" && (c.FromDate is null || c.FromDate > c.ToDate)) ||
                (c.EvidenceKind == "NoPriorActivity" && c.FromDate is not null))) errors[key + ".evidenceKind"] = ["Provide a statement range or a dated no-prior-activity declaration."];
            if (c.Classes is null || c.Classes.Count > 100 || (c.Included && c.AttestedComplete && c.Classes.Count == 0)) errors[key + ".classes"] = ["Inventory expected transaction classes before attesting completeness (maximum 100)."];
            if (c.Classes is null) continue;
            for (var j = 0; j < c.Classes.Count; j++)
            {
                var item = c.Classes[j]; var itemKey = key + $".classes[{j}]";
                if (item is null) { errors[itemKey] = ["A class is required."]; continue; }
                CheckText(errors, itemKey + ".label", item.Label, 160, true);
                CheckText(errors, itemKey + ".sourceReference", item.SourceReference, 500);
                CheckText(errors, itemKey + ".policyReference", item.PolicyReference, 500);
                CheckText(errors, itemKey + ".reconciliationReference", item.ReconciliationReference, 500);
                CheckText(errors, itemKey + ".prerequisiteReference", item.PrerequisiteReference, 500);
            }
        }
        return errors;
    }
    private static void CheckText(Dictionary<string, string[]> errors, string key, string? value, int maximum, bool required = false)
    {
        if ((required && string.IsNullOrWhiteSpace(value)) || value?.Length > maximum || value?.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t') == true)
            errors[key] = [$"Use {(required ? "1–" : "up to ")}{maximum} characters."];
    }
    public static AccountingSetupResponse Setup(AccountingConfiguration configuration, string version, IReadOnlyList<AccountingAccountResponse> accounts)
    {
        var p = configuration.Policies; var missing = new List<string>();
        if (p.Country is null) missing.Add("Choose a country.");
        if (AccountingCatalog.Value.Countries.SingleOrDefault(c => c.Code == p.Country)?.Regions.Count > 0 && p.Region is null) missing.Add("Choose a state or region.");
        if (p.Currency is null) missing.Add("Choose a functional currency.");
        if (p.Scale is null) missing.Add("Confirm the posting scale.");
        if (p.FiscalStartMonth is null) missing.Add("Choose the fiscal start month.");
        if (p.StartApproach is null || p.PlannedStartDate is null) missing.Add("Choose a start approach and planned date.");
        if (p.RetentionYears is null || string.IsNullOrWhiteSpace(p.RetentionRationale)) missing.Add("Record proposed document retention and its rationale.");
        foreach (var slot in AccountingCatalog.Slots.Take(4)) if (!configuration.Mappings.Any(m => m.Slot == slot)) missing.Add($"Map {slot}.");
        var funding = accounts.Where(a => !a.IsArchived && a.Purpose is "Bank" or "Cash" or "CardLiability").ToArray();
        if (!funding.Any(a => configuration.Coverage.Any(c => c.AccountId == a.Id && c.Included))) missing.Add("Include at least one funding account in coverage.");
        foreach (var account in funding)
        {
            var coverage = configuration.Coverage.SingleOrDefault(c => c.AccountId == account.Id);
            if (coverage is null || (coverage.Included && !coverage.AttestedComplete)) missing.Add($"Complete the coverage inventory for {account.Code}.");
        }
        var blockers = new List<string> { "Bookkeeping is not yet available. Journal, recognition, evidence retention, cutover and reconciliation prerequisites remain outstanding." };
        if (string.IsNullOrWhiteSpace(p.FrameworkNotes)) blockers.Add("Reporting framework and tax-policy decisions remain unresolved.");
        foreach (var slot in AccountingCatalog.Slots.Skip(4)) if (!configuration.Mappings.Any(m => m.Slot == slot)) blockers.Add($"Classification candidate {slot} is not configured.");
        foreach (var c in configuration.Coverage.Where(c => c.Included)) foreach (var item in c.Classes) blockers.Add($"Unsupported transaction class: {item.Label}.");
        return new(configuration, version, missing.Count == 0, false, missing, blockers);
    }
}
