-- Copyright (c) 2026 The White Stag Collection.
ALTER PROCEDURE [Security].[ReadDatabaseReadiness]
@ExpectedMigration nvarchar(150)=N'20261008010000_AddFinancialEvidenceRetention' WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SELECT
        CONVERT(bit, CASE WHEN
            (SELECT MAX([MigrationId]) FROM [dbo].[__EFMigrationsHistory]) =
                @ExpectedMigration
            THEN 1 ELSE 0 END) AS [CompatibleMigration],
        CONVERT(bit, CASE WHEN EXISTS
        (
            SELECT 1 FROM sys.security_policies
            WHERE [name] = N'TenantIsolationPolicy'
                AND SCHEMA_NAME([schema_id]) = N'Security' AND [is_enabled] = 1
        ) THEN 1 ELSE 0 END) AS [TenantPolicyEnabled],
        CONVERT(bit, CASE WHEN EXISTS
        (
            SELECT 1 FROM sys.database_permissions
            WHERE [grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'workbench_web')
                AND [class] = 0 AND [permission_name] = N'ALTER ANY SECURITY POLICY'
                AND [state] = N'D'
        ) THEN 1 ELSE 0 END) AS [TenantPolicyAlterDenied],
        CONVERT(bit, CASE WHEN
        (
            SELECT COUNT(DISTINCT [permission_name]) FROM sys.database_permissions
            WHERE [grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'workbench_web')
                AND [class] = 1
                AND [major_id] = OBJECT_ID(N'[Identity].[DataProtectionKeys]')
                AND [permission_name] IN (N'SELECT', N'INSERT', N'UPDATE', N'DELETE')
                AND [state] IN (N'G', N'W')
        ) = 4 THEN 1 ELSE 0 END) AS [KeyTableAvailable],
        [state].[RestoreGeneration],
        [state].[RestoreSanitizedGeneration],
        [restore].[IsPending] AS [RestorePending],
        CONVERT(bit, CASE WHEN EXISTS
        (
            SELECT 1 FROM sys.database_permissions
            WHERE [grantee_principal_id] = DATABASE_PRINCIPAL_ID(N'workbench_web')
                AND [class] = 1
                AND [major_id] = OBJECT_ID(N'[Security].[TenantContextKeys]')
                AND [permission_name] = N'SELECT' AND [state] = N'D'
        ) THEN 1 ELSE 0 END) AS [TenantContextKeyProtected],
        CONVERT(bit, CASE WHEN OBJECT_ID(N'[Security].[TryAcquireSensitiveRequest]', N'P') IS NOT NULL
            THEN 1 ELSE 0 END) AS [SensitiveLimiterAvailable],
    CONVERT(bit,CASE WHEN
        (SELECT COUNT(*) FROM sys.procedures WHERE schema_id=SCHEMA_ID(N'ServiceAdministration')
            AND name IN(N'FindAccountForLogin',N'CreateSession',N'ResolveSession',N'RevokeSession'))=4
        AND OBJECT_ID(N'ServiceAdministration.RehashPassword') IS NULL
        AND (SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id=DATABASE_PRINCIPAL_ID(N'workbench_web')
            AND major_id IN(OBJECT_ID(N'ServiceAdministration.FindAccountForLogin'),OBJECT_ID(N'ServiceAdministration.CreateSession'),OBJECT_ID(N'ServiceAdministration.ResolveSession'),OBJECT_ID(N'ServiceAdministration.RevokeSession'))
            AND permission_name=N'EXECUTE' AND state=N'G')=4
        AND (SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id IN(DATABASE_PRINCIPAL_ID(N'workbench_web'),DATABASE_PRINCIPAL_ID(N'workbench_worker'),DATABASE_PRINCIPAL_ID(N'workbench_operator'))
            AND major_id IN(OBJECT_ID(N'ServiceAdministration.Accounts'),OBJECT_ID(N'ServiceAdministration.Sessions'))
            AND permission_name IN(N'SELECT',N'INSERT',N'UPDATE',N'DELETE') AND state=N'D')=24
        AND (SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id=DATABASE_PRINCIPAL_ID(N'workbench_operator')
            AND major_id IN(OBJECT_ID(N'Administration.ProvisionServiceAdmin'),OBJECT_ID(N'Administration.DisableServiceAdmin'),OBJECT_ID(N'Administration.ResetServiceAdminPassword'),OBJECT_ID(N'Administration.RevokeServiceAdminSessions'))
            AND permission_name=N'EXECUTE' AND state=N'G')=4
        AND NOT EXISTS(SELECT 1 FROM sys.database_permissions WHERE permission_name=N'EXECUTE' AND state IN(N'G',N'W')
            AND ((grantee_principal_id IN(DATABASE_PRINCIPAL_ID(N'workbench_web'),DATABASE_PRINCIPAL_ID(N'workbench_worker'),DATABASE_PRINCIPAL_ID(N'public'))
                AND major_id IN(OBJECT_ID(N'Administration.ProvisionServiceAdmin'),OBJECT_ID(N'Administration.DisableServiceAdmin'),OBJECT_ID(N'Administration.ResetServiceAdminPassword'),OBJECT_ID(N'Administration.RevokeServiceAdminSessions')))
            OR (grantee_principal_id IN(DATABASE_PRINCIPAL_ID(N'workbench_operator'),DATABASE_PRINCIPAL_ID(N'workbench_worker'),DATABASE_PRINCIPAL_ID(N'public'))
                AND major_id IN(OBJECT_ID(N'ServiceAdministration.FindAccountForLogin'),OBJECT_ID(N'ServiceAdministration.CreateSession'),OBJECT_ID(N'ServiceAdministration.ResolveSession'),OBJECT_ID(N'ServiceAdministration.RevokeSession')))))
        THEN 1 ELSE 0 END) AS [ServiceAdminIdentityReady]
    FROM [Security].[DatabaseSecurityState] AS [state]
    CROSS JOIN [Security].[WorkbenchRestorePending] AS [restore]
    WHERE [state].[Id] = 1 AND [restore].[Id] = 1;
END;
