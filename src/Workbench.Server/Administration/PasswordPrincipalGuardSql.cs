// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Administration;

public static partial class PasswordPrincipalProvisioning
{
    private const string GuardPrefixSql = """
        DECLARE @result int;
        EXEC @result = sys.sp_getapplock @Resource=N'Workbench.PasswordProvisioning',
            @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=30000;
        IF @result < 0 THROW 50030, 'Principal provisioning lock unavailable.', 1;
        -- Repair only accidental direct EXECUTE grants on the internal journal/period kernels.
        -- Keep this before the general role guard so unrelated unsafe grants still fail closed.
        IF EXISTS(SELECT 1 FROM sys.database_permissions WHERE class=1 AND minor_id=0
            AND major_id=OBJECT_ID(N'[Accounting].[PostJournal]') AND permission_name=N'EXECUTE'
            AND grantee_principal_id=DATABASE_PRINCIPAL_ID(N'workbench_web') AND state IN ('G','W'))
            REVOKE EXECUTE ON OBJECT::[Accounting].[PostJournal] FROM [workbench_web];
        IF EXISTS(SELECT 1 FROM sys.database_permissions WHERE class=1 AND minor_id=0
            AND major_id=OBJECT_ID(N'[Accounting].[PostJournal]') AND permission_name=N'EXECUTE'
            AND grantee_principal_id=DATABASE_PRINCIPAL_ID(N'workbench_worker') AND state IN ('G','W'))
            REVOKE EXECUTE ON OBJECT::[Accounting].[PostJournal] FROM [workbench_worker];
        IF EXISTS(SELECT 1 FROM sys.database_permissions WHERE class=1 AND minor_id=0
            AND major_id=OBJECT_ID(N'[Accounting].[PostJournal]') AND permission_name=N'EXECUTE'
            AND grantee_principal_id=DATABASE_PRINCIPAL_ID(N'public') AND state IN ('G','W'))
            REVOKE EXECUTE ON OBJECT::[Accounting].[PostJournal] FROM [public];
        IF EXISTS(SELECT 1 FROM sys.database_permissions WHERE class=1 AND minor_id=0
            AND major_id=OBJECT_ID(N'[Accounting].[EnsureOpenPeriod]') AND permission_name=N'EXECUTE'
            AND grantee_principal_id=DATABASE_PRINCIPAL_ID(N'workbench_web') AND state IN ('G','W'))
            REVOKE EXECUTE ON OBJECT::[Accounting].[EnsureOpenPeriod] FROM [workbench_web];
        IF EXISTS(SELECT 1 FROM sys.database_permissions WHERE class=1 AND minor_id=0
            AND major_id=OBJECT_ID(N'[Accounting].[EnsureOpenPeriod]') AND permission_name=N'EXECUTE'
            AND grantee_principal_id=DATABASE_PRINCIPAL_ID(N'workbench_worker') AND state IN ('G','W'))
            REVOKE EXECUTE ON OBJECT::[Accounting].[EnsureOpenPeriod] FROM [workbench_worker];
        IF EXISTS(SELECT 1 FROM sys.database_permissions WHERE class=1 AND minor_id=0
            AND major_id=OBJECT_ID(N'[Accounting].[EnsureOpenPeriod]') AND permission_name=N'EXECUTE'
            AND grantee_principal_id=DATABASE_PRINCIPAL_ID(N'public') AND state IN ('G','W'))
            REVOKE EXECUTE ON OBJECT::[Accounting].[EnsureOpenPeriod] FROM [public];
        IF EXISTS(SELECT 1 FROM sys.database_permissions WHERE class=1 AND minor_id=0
            AND major_id=OBJECT_ID(N'[Accounting].[ClosePeriod]') AND permission_name=N'EXECUTE'
            AND grantee_principal_id=DATABASE_PRINCIPAL_ID(N'workbench_web') AND state IN ('G','W'))
            REVOKE EXECUTE ON OBJECT::[Accounting].[ClosePeriod] FROM [workbench_web];
        IF EXISTS(SELECT 1 FROM sys.database_permissions WHERE class=1 AND minor_id=0
            AND major_id=OBJECT_ID(N'[Accounting].[ClosePeriod]') AND permission_name=N'EXECUTE'
            AND grantee_principal_id=DATABASE_PRINCIPAL_ID(N'workbench_worker') AND state IN ('G','W'))
            REVOKE EXECUTE ON OBJECT::[Accounting].[ClosePeriod] FROM [workbench_worker];
        IF EXISTS(SELECT 1 FROM sys.database_permissions WHERE class=1 AND minor_id=0
            AND major_id=OBJECT_ID(N'[Accounting].[ClosePeriod]') AND permission_name=N'EXECUTE'
            AND grantee_principal_id=DATABASE_PRINCIPAL_ID(N'public') AND state IN ('G','W'))
            REVOKE EXECUTE ON OBJECT::[Accounting].[ClosePeriod] FROM [public];
        IF EXISTS(SELECT 1 FROM sys.database_permissions WHERE class=1 AND minor_id=0
            AND major_id=OBJECT_ID(N'[Accounting].[CorrectJournal]') AND permission_name=N'EXECUTE'
            AND grantee_principal_id=DATABASE_PRINCIPAL_ID(N'workbench_web') AND state IN ('G','W'))
            REVOKE EXECUTE ON OBJECT::[Accounting].[CorrectJournal] FROM [workbench_web];
        IF EXISTS(SELECT 1 FROM sys.database_permissions WHERE class=1 AND minor_id=0
            AND major_id=OBJECT_ID(N'[Accounting].[CorrectJournal]') AND permission_name=N'EXECUTE'
            AND grantee_principal_id=DATABASE_PRINCIPAL_ID(N'workbench_worker') AND state IN ('G','W'))
            REVOKE EXECUTE ON OBJECT::[Accounting].[CorrectJournal] FROM [workbench_worker];
        IF EXISTS(SELECT 1 FROM sys.database_permissions WHERE class=1 AND minor_id=0
            AND major_id=OBJECT_ID(N'[Accounting].[CorrectJournal]') AND permission_name=N'EXECUTE'
            AND grantee_principal_id=DATABASE_PRINCIPAL_ID(N'public') AND state IN ('G','W'))
            REVOKE EXECUTE ON OBJECT::[Accounting].[CorrectJournal] FROM [public];
        -- Repair only named recognition kernels; preserve explicit DENYs and reject other unsafe grants.
        DECLARE @RecognitionRepair nvarchar(max)=N'';
        SELECT @RecognitionRepair=COALESCE(STRING_AGG(CONVERT(nvarchar(max),N'REVOKE EXECUTE ON OBJECT::'+QUOTENAME(OBJECT_SCHEMA_NAME(p.major_id))+N'.'+QUOTENAME(OBJECT_NAME(p.major_id))+N' FROM '+QUOTENAME(USER_NAME(p.grantee_principal_id))+N';'),N''),N'')
          FROM sys.database_permissions p WHERE p.class=1 AND p.minor_id=0 AND p.permission_name=N'EXECUTE' AND p.state IN('G','W')
            AND p.major_id IN(OBJECT_ID(N'Purchasing.PostRecognition'),OBJECT_ID(N'Purchasing.CorrectRecognition'))
            AND p.grantee_principal_id IN(DATABASE_PRINCIPAL_ID(N'workbench_web'),DATABASE_PRINCIPAL_ID(N'workbench_worker'),DATABASE_PRINCIPAL_ID(N'public'));
        EXEC sys.sp_executesql @RecognitionRepair;
        -- Web/operator grants must match their migration-defined object access. DENY rows add no authority.
        -- Keep this allowlist and the successful provisioning test current when adding migration grants.
        -- Migrator intentionally retains database CONTROL and is not a restricted workload role.
        IF EXISTS
        (
            SELECT 1 FROM sys.database_permissions AS permission
            WHERE permission.grantee_principal_id IN
                (DATABASE_PRINCIPAL_ID(N'workbench_web'), DATABASE_PRINCIPAL_ID(N'workbench_operator'))
                AND permission.state IN ('G', 'W')
                AND NOT
                (
                    permission.state='G' AND permission.minor_id=0 AND
                    (
                        (permission.class=0 AND permission.permission_name=N'CONNECT') OR
                        (permission.class=1 AND EXISTS
                        (
                            SELECT 1 FROM (VALUES
        """;

    private const string GuardSuffixSql = """
                            ) AS allowed(RoleName, ObjectName, PermissionNames)
                            CROSS APPLY STRING_SPLIT(allowed.PermissionNames, ',') AS allowedPermission
                            WHERE DATABASE_PRINCIPAL_ID(allowed.RoleName)=permission.grantee_principal_id
                                AND OBJECT_ID(allowed.ObjectName)=permission.major_id
                                AND allowedPermission.value COLLATE DATABASE_DEFAULT=
                                    permission.permission_name COLLATE DATABASE_DEFAULT
                        ))
                    )
                )
        ) THROW 50030, 'Destination role has incompatible direct authority.', 1;
        """;
}
