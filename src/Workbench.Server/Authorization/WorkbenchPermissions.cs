// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Authorization;

public static class WorkbenchPermissions
{
    public const string TenantAccess = "TenantAccess";
    public const string SupplierBillsManage = "SupplierBillsManage";
    public const string SupplierBillsPost = "SupplierBillsPost";
    public const string SupplierPaymentsRecord = "SupplierPaymentsRecord";
    public const string SupplierPaymentsCorrect = "SupplierPaymentsCorrect";
    public const string SupplierAllocationsManage = "SupplierAllocationsManage";
    public const string SupplierCreditsPost = "SupplierCreditsPost";
    public const string SupplierCreditsCorrect = "SupplierCreditsCorrect";
    public const string SupplierRefundsRecord = "SupplierRefundsRecord";
    public const string SupplierRefundsCorrect = "SupplierRefundsCorrect";
    public const string SupplierRefundRepaymentsRecord = "SupplierRefundRepaymentsRecord";
    public const string SupplierRefundRepaymentsCorrect = "SupplierRefundRepaymentsCorrect";
    public const string SupplierBillsCorrect = "SupplierBillsCorrect";

    public const string TenantUsersManage = "TenantUsersManage";
    public const string AccountingConfigurationRead = "AccountingConfigurationRead";
    public const string AccountingConfigurationManage = "AccountingConfigurationManage";
    public const string AccountingReportsRead = "AccountingReportsRead";
    public const string AccountingReportsExport = "AccountingReportsExport";
    public const string AccountingReconcile = "AccountingReconcile";
    public const string AccountingPeriodsClose = "AccountingPeriodsClose";
    public const string AccountingFiscalYearsClose = "AccountingFiscalYearsClose";
    public static readonly string[] AccountingReaderPermissions = [AccountingReportsRead, AccountingReportsExport];
    public static readonly string[] AccountingAdministratorPermissions =
        [AccountingConfigurationRead, AccountingConfigurationManage, AccountingReportsRead, AccountingReportsExport,
         AccountingReconcile, AccountingPeriodsClose, AccountingFiscalYearsClose];
}
