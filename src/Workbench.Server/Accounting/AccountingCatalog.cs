// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Accounting;

public static class AccountingCatalog
{
    public static readonly string[] Types = ["Asset", "Liability", "Equity", "Income", "Expense"];
    public static readonly string[] Purposes = ["General", "Bank", "Cash", "CardLiability", "SupplierPayable", "SupplierAdvance", "SupplierCreditReceivable", "SupplierRefundClearing"];
    public static readonly string[] Slots = ["SupplierPayable", "SupplierAdvance", "SupplierCreditReceivable", "SupplierRefundClearing", "Inventory", "Expense", "Prepayment", "RecoverableTax"];
    public static readonly AccountingConfiguration Empty = new(new(null, null, null, null, null, null, null, null, null, null), [], []);
    private static AccountingOption[] Regions(string source) => source.Split('|').Select(value => { var parts = value.Split(':'); return new AccountingOption(parts[0], parts[1]); }).ToArray();
    public static readonly AccountingCatalogResponse Value = new("2026-09-21", [
        new("US", "United States", Regions("AL:Alabama|AK:Alaska|AZ:Arizona|AR:Arkansas|CA:California|CO:Colorado|CT:Connecticut|DE:Delaware|DC:District of Columbia|FL:Florida|GA:Georgia|HI:Hawaii|ID:Idaho|IL:Illinois|IN:Indiana|IA:Iowa|KS:Kansas|KY:Kentucky|LA:Louisiana|ME:Maine|MD:Maryland|MA:Massachusetts|MI:Michigan|MN:Minnesota|MS:Mississippi|MO:Missouri|MT:Montana|NE:Nebraska|NV:Nevada|NH:New Hampshire|NJ:New Jersey|NM:New Mexico|NY:New York|NC:North Carolina|ND:North Dakota|OH:Ohio|OK:Oklahoma|OR:Oregon|PA:Pennsylvania|RI:Rhode Island|SC:South Carolina|SD:South Dakota|TN:Tennessee|TX:Texas|UT:Utah|VT:Vermont|VA:Virginia|WA:Washington|WV:West Virginia|WI:Wisconsin|WY:Wyoming|AS:American Samoa|GU:Guam|MP:Northern Mariana Islands|PR:Puerto Rico|VI:US Virgin Islands")),
        new("CA", "Canada", Regions("AB:Alberta|BC:British Columbia|MB:Manitoba|NB:New Brunswick|NL:Newfoundland and Labrador|NS:Nova Scotia|NT:Northwest Territories|NU:Nunavut|ON:Ontario|PE:Prince Edward Island|QC:Quebec|SK:Saskatchewan|YT:Yukon")),
        new("AU", "Australia", Regions("ACT:Australian Capital Territory|NSW:New South Wales|NT:Northern Territory|QLD:Queensland|SA:South Australia|TAS:Tasmania|VIC:Victoria|WA:Western Australia")),
        new("GB", "United Kingdom", []), new("NZ", "New Zealand", []), new("DE", "Germany", []),
        new("FR", "France", []), new("JP", "Japan", []), new("CH", "Switzerland", []), new("IN", "India", [])
    ], [new("USD", "US dollar", 2), new("CAD", "Canadian dollar", 2), new("GBP", "Pound sterling", 2),
        new("EUR", "Euro", 2), new("AUD", "Australian dollar", 2), new("NZD", "New Zealand dollar", 2),
        new("JPY", "Japanese yen", 0), new("CHF", "Swiss franc", 2), new("INR", "Indian rupee", 2),
        new("CNY", "Chinese yuan", 2), new("KWD", "Kuwaiti dinar", 3)], Types, Purposes, Slots, [
        new("1000", "Bank", "Asset", "Bank", null), new("1010", "Cash", "Asset", "Cash", null),
        new("1100", "Receivables", "Asset", "General", null), new("1200", "Inventory", "Asset", "General", null),
        new("1300", "Prepayments", "Asset", "General", null), new("1400", "Equipment", "Asset", "General", null),
        new("1500", "Recoverable tax", "Asset", "General", null), new("1600", "Supplier advances", "Asset", "SupplierAdvance", null),
        new("1700", "Supplier credits", "Asset", "SupplierCreditReceivable", null),
        new("2000", "Supplier payables", "Liability", "SupplierPayable", null),
        new("2100", "Supplier refund clearing", "Liability", "SupplierRefundClearing", null),
        new("2200", "Tax payable", "Liability", "General", null), new("2300", "Card liability", "Liability", "CardLiability", null),
        new("3000", "Equity", "Equity", "General", null), new("4000", "Income", "Income", "General", null),
        new("5000", "Cost of sales", "Expense", "General", null), new("6000", "Expenses", "Expense", "General", null)
    ]);
    public static string? RequiredType(string purpose) => purpose switch
    {
        "Bank" or "Cash" or "SupplierAdvance" or "SupplierCreditReceivable" or "Inventory" or "Prepayment" or "RecoverableTax" => "Asset",
        "CardLiability" or "SupplierPayable" or "SupplierRefundClearing" => "Liability",
        "Expense" => "Expense",
        _ => null
    };
}
