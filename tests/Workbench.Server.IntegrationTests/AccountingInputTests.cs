// Copyright (c) 2026 The White Stag Collection.
using Workbench.Server.Accounting;
using Xunit;

namespace Workbench.Server.IntegrationTests;

public sealed class AccountingInputTests
{
    private static AccountingConfiguration Empty => new(new(null, null, null, null, null, null, null, null, null, null), [], []);

    [Fact]
    public void IncompleteConfigurationIsAllowedButMalformedValuesAreRejected()
    {
        // GIVEN a resumable setup with no policies chosen.
        var configuration = Empty;
        Assert.Empty(AccountingInput.Validate(configuration));
        // WHEN supplied policies are malformed THEN each value is rejected, not treated as missing.
        var errors = AccountingInput.Validate(configuration with
        {
            Policies = configuration.Policies with
            { Country = "ZZ", Currency = "???", Scale = 5, FiscalStartMonth = 13, StartApproach = "IgnoreHistory", RetentionYears = 0 }
        });
        Assert.Contains("policies.country", errors.Keys);
        Assert.Contains("policies.currency", errors.Keys);
        Assert.Contains("policies.scale", errors.Keys);
        Assert.Contains("policies.fiscalStartMonth", errors.Keys);
        Assert.Contains("policies.startApproach", errors.Keys);
        Assert.Contains("policies.retentionYears", errors.Keys);
    }

    [Theory]
    [InlineData("Asset", "Bank", true)]
    [InlineData("Expense", "Bank", false)]
    [InlineData("Liability", "SupplierPayable", true)]
    [InlineData("Asset", "SupplierPayable", false)]
    [InlineData("Equity", "General", true)]
    [InlineData("Income", "General", true)]
    [InlineData("Unknown", "General", false)]
    public void AccountPurposeRequiresItsFinancialType(string type, string purpose, bool valid)
    {
        // GIVEN a general business account, independent of business activity.
        var accounts = new[] { new AccountingAccountContent("100", "Account", type, purpose, null) };
        // WHEN checking financial eligibility THEN incompatible type/purpose pairs fail.
        Assert.Equal(valid, AccountingInput.ValidateAccounts(accounts).Count == 0);
    }

    [Fact]
    public void DuplicateCodesAndBlankAccountsCannotEnterAChart()
    {
        // GIVEN a starter chart with codes differing only by letter case.
        var accounts = new[] { new AccountingAccountContent("cash", "Cash", "Asset", "Cash", null),
            new AccountingAccountContent("CASH", "", "Asset", "General", null) };
        // WHEN validating an atomic chart THEN duplicate identity and missing description are reported.
        Assert.NotEmpty(AccountingInput.ValidateAccounts(accounts));
        Assert.NotEmpty(AccountingInput.ValidateAccounts([]));
    }

    [Fact]
    public void CoverageRequiresTruthfulEvidenceAndDistinctReferences()
    {
        // GIVEN coverage that claims a statement without dates and repeats an account.
        var coverage = new AccountingCoverage(Guid.NewGuid(), true, null, "Statement", null, null, null, null, true, []);
        var configuration = Empty with { Coverage = [coverage, coverage] };
        // WHEN validated THEN an attestation cannot replace evidence or hide duplicated accounts.
        Assert.NotEmpty(AccountingInput.Validate(configuration));
        // AND a genuine no-prior-activity declaration is supported without fabricated statements.
        configuration = Empty with
        {
            Coverage = [coverage with { EvidenceKind = "NoPriorActivity",
            ToDate = new DateOnly(2026, 1, 1), Rationale = "New account", Classes = [new("Supplier payment", null, null, null, "BK-06")] }]
        };
        Assert.Empty(AccountingInput.Validate(configuration));
    }

    [Fact]
    public void CompleteSetupStillReportsEveryUnsupportedClassAndNeverActivatesBooks()
    {
        // GIVEN policies, all four controls and a fully attested funding-account inventory.
        var controls = AccountingCatalog.Slots.Take(4).Select((slot, index) => new AccountingAccountResponse(Guid.NewGuid(), $"2{index}", slot,
            AccountingCatalog.RequiredType(slot)!, slot, null, false, Guid.NewGuid().ToString("D"))).ToArray();
        var bank = new AccountingAccountResponse(Guid.NewGuid(), "100", "Bank", "Asset", "Bank", null, false, Guid.NewGuid().ToString("D"));
        var setup = Empty with
        {
            Policies = new("CA", "BC", "CAD", 2, 4, "FromBeginning", new(2026, 1, 1), 7, "Proposed policy", null),
            Mappings = controls.Select(a => new AccountingMapping(a.Purpose, a.Id)).ToArray(),
            Coverage = [new(bank.Id, true, null, "NoPriorActivity", null, new(2026, 1, 1), null, "New account", true,
                [new("Supplier payments", null, null, null, "BK-06"), new("Sales receipts", null, null, null, null)])]
        };
        // WHEN readback computes completeness THEN unsupported receipts remain explicit despite complete setup.
        var result = AccountingInput.Setup(setup, Guid.Empty.ToString("D"), [.. controls, bank]);
        Assert.True(result.SetupComplete); Assert.False(result.BookkeepingAvailable);
        Assert.Contains(result.Blockers, b => b.Contains("Sales receipts", StringComparison.Ordinal));
        Assert.Contains(result.Blockers, b => b.Contains("Supplier payments", StringComparison.Ordinal));
        // AND an unreviewed additional bank account or missing region makes the plan incomplete.
        Assert.False(AccountingInput.Setup(setup, "version", [.. controls, bank, bank with { Id = Guid.NewGuid(), Code = "101" }]).SetupComplete);
        Assert.False(AccountingInput.Setup(setup with { Policies = setup.Policies with { Region = null } }, "version", [.. controls, bank]).SetupComplete);
    }
}
