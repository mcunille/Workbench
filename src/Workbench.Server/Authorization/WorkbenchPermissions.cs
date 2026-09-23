// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Authorization;

public static class WorkbenchPermissions
{
    public const string TenantAccess = "TenantAccess";

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
