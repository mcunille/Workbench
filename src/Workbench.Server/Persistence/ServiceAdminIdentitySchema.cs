// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class ServiceAdminIdentitySchema
{
    internal static void Up(MigrationBuilder migration, string migrationId)
    {
        // SQL-owned command storage: never mapped into the tenant EF context.
        migration.Sql("""
            CREATE SCHEMA [ServiceAdministration] AUTHORIZATION [dbo];
            """);
        migration.Sql("""
            CREATE TABLE ServiceAdministration.Accounts
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY CHECK(Id<>'00000000-0000-0000-0000-000000000000'),
                Email nvarchar(256) NOT NULL CHECK(LEN(LTRIM(RTRIM(Email)))>0),
                NormalizedEmail nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL UNIQUE CHECK(LEN(LTRIM(RTRIM(NormalizedEmail)))>0),
                PasswordHash nvarchar(1024) NOT NULL CHECK(LEN(PasswordHash)>0),
                IsEnabled bit NOT NULL,
                SecurityVersion bigint NOT NULL CHECK(SecurityVersion>0),
                CreatedAtUtc datetimeoffset NOT NULL
            );
            CREATE TABLE ServiceAdministration.Sessions
            (
                Id uniqueidentifier NOT NULL PRIMARY KEY CHECK(Id<>'00000000-0000-0000-0000-000000000000'),
                AccountId uniqueidentifier NOT NULL REFERENCES ServiceAdministration.Accounts(Id),
                SecurityVersion bigint NOT NULL CHECK(SecurityVersion>0),
                TokenHash binary(32) NOT NULL UNIQUE,
                CreatedAtUtc datetimeoffset NOT NULL,
                LastSeenAtUtc datetimeoffset NOT NULL,
                IdleExpiresAtUtc datetimeoffset NOT NULL,
                AbsoluteExpiresAtUtc datetimeoffset NOT NULL,
                RevokedAtUtc datetimeoffset NULL,
                RevocationReason nvarchar(40) NULL,
                CONSTRAINT CK_ServiceAdminSessions_Expiry CHECK(CreatedAtUtc<IdleExpiresAtUtc AND IdleExpiresAtUtc<=AbsoluteExpiresAtUtc AND CreatedAtUtc<=LastSeenAtUtc AND LastSeenAtUtc<AbsoluteExpiresAtUtc),
                CONSTRAINT CK_ServiceAdminSessions_Revocation CHECK((RevokedAtUtc IS NULL AND RevocationReason IS NULL) OR (RevokedAtUtc IS NOT NULL AND RevocationReason IS NOT NULL AND RevocationReason IN(N'Logout',N'Disabled',N'PasswordReset',N'OperatorRevoked')))
            );
            CREATE INDEX IX_ServiceAdminSessions_AccountId ON ServiceAdministration.Sessions(AccountId);
            """);
        migration.Sql("""
            CREATE PROCEDURE Administration.ProvisionServiceAdmin
                @AccountId uniqueidentifier,@Email nvarchar(max),@NormalizedEmail nvarchar(max),@PasswordHash nvarchar(max),@Now datetimeoffset
            WITH EXECUTE AS OWNER AS
            BEGIN
                SET NOCOUNT ON; SET XACT_ABORT ON;
                IF @AccountId IS NULL OR @AccountId='00000000-0000-0000-0000-000000000000' OR @Now IS NULL
                    OR @Email IS NULL OR LEN(LTRIM(RTRIM(@Email)))=0 OR DATALENGTH(@Email)>512
                    OR @NormalizedEmail IS NULL OR LEN(LTRIM(RTRIM(@NormalizedEmail)))=0 OR DATALENGTH(@NormalizedEmail)>512
                    OR @PasswordHash IS NULL OR LEN(@PasswordHash)=0 OR DATALENGTH(@PasswordHash)>2048
                    THROW 50040,'Invalid service-admin account input.',1;
                BEGIN TRANSACTION;
                INSERT ServiceAdministration.Accounts(Id,Email,NormalizedEmail,PasswordHash,IsEnabled,SecurityVersion,CreatedAtUtc)
                    VALUES(@AccountId,@Email,@NormalizedEmail,@PasswordHash,1,1,@Now);
                INSERT Security.SystemSecurityAuditEvents(Id,Action,Outcome,CorrelationId,OccurredAtUtc)
                    VALUES(NEWID(),N'service-admin.provisioned',N'Succeeded',CONVERT(nvarchar(36),@AccountId),@Now);
                COMMIT;
                SELECT @AccountId AS Id;
            END;
            """);
        foreach (var (name, change, reason, action) in new[]
        {
            ("DisableServiceAdmin", ",IsEnabled=0", "Disabled", "disabled"),
            ("ResetServiceAdminPassword", ",PasswordHash=@PasswordHash", "PasswordReset", "password-reset"),
            ("RevokeServiceAdminSessions", "", "OperatorRevoked", "sessions-revoked"),
        })
        {
            var reset = name == "ResetServiceAdminPassword";
            migration.Sql($"""
                CREATE PROCEDURE Administration.{name}
                    @AccountId uniqueidentifier,@Now datetimeoffset{(reset ? ",@PasswordHash nvarchar(max)" : "")}
                WITH EXECUTE AS OWNER AS
                BEGIN
                    SET NOCOUNT ON; SET XACT_ABORT ON;
                    IF @AccountId IS NULL OR @Now IS NULL THROW 50040,'Invalid service-admin maintenance input.',1;
                    {(reset ? "IF @PasswordHash IS NULL OR LEN(@PasswordHash)=0 OR DATALENGTH(@PasswordHash)>2048 THROW 50040,'Invalid service-admin password hash.',1;" : "")}
                    BEGIN TRANSACTION;
                    UPDATE ServiceAdministration.Accounts WITH(UPDLOCK,HOLDLOCK)
                        SET SecurityVersion=SecurityVersion+1{change} WHERE Id=@AccountId;
                    IF @@ROWCOUNT=0 BEGIN ROLLBACK; THROW 50041,'Service-admin account was not found.',1; END;
                    UPDATE ServiceAdministration.Sessions SET RevokedAtUtc=@Now,RevocationReason=N'{reason}'
                        WHERE AccountId=@AccountId AND RevokedAtUtc IS NULL;
                    INSERT Security.SystemSecurityAuditEvents(Id,Action,Outcome,CorrelationId,OccurredAtUtc)
                        VALUES(NEWID(),N'service-admin.{action}',N'Succeeded',CONVERT(nvarchar(36),@AccountId),@Now);
                    COMMIT;
                END;
                """);
        }
        migration.Sql("""
            CREATE PROCEDURE ServiceAdministration.FindAccountForLogin @NormalizedEmail nvarchar(max)
            AS
            BEGIN
                SET NOCOUNT ON;
                SELECT Id,Email,NormalizedEmail,PasswordHash,IsEnabled,SecurityVersion,CreatedAtUtc
                FROM ServiceAdministration.Accounts WHERE NormalizedEmail=@NormalizedEmail AND IsEnabled=1
                    AND DATALENGTH(@NormalizedEmail)<=512
                    AND NOT EXISTS(SELECT 1 FROM Security.WorkbenchRestorePending WHERE IsPending=1);
            END;
            """);
        migration.Sql("""
            CREATE PROCEDURE ServiceAdministration.CreateSession
                @SessionId uniqueidentifier,@AccountId uniqueidentifier,@SecurityVersion bigint,@TokenHash varbinary(max),
                @Now datetimeoffset,@IdleExpiresAtUtc datetimeoffset,@AbsoluteExpiresAtUtc datetimeoffset
            AS
            BEGIN
                SET NOCOUNT ON; SET XACT_ABORT ON;
                IF @SessionId IS NULL OR @SessionId='00000000-0000-0000-0000-000000000000'
                    OR @AccountId IS NULL OR @SecurityVersion IS NULL OR @SecurityVersion<=0
                    OR @TokenHash IS NULL OR DATALENGTH(@TokenHash)<>32 OR @Now IS NULL
                    OR @IdleExpiresAtUtc IS NULL OR @AbsoluteExpiresAtUtc IS NULL
                    OR @Now>=@IdleExpiresAtUtc OR @IdleExpiresAtUtc>@AbsoluteExpiresAtUtc
                    THROW 50040,'Invalid service-admin session input.',1;
                BEGIN TRANSACTION;
                IF EXISTS(SELECT 1 FROM ServiceAdministration.Accounts WITH(UPDLOCK,HOLDLOCK)
                    WHERE Id=@AccountId AND IsEnabled=1 AND SecurityVersion=@SecurityVersion)
                    AND NOT EXISTS(SELECT 1 FROM Security.WorkbenchRestorePending WHERE IsPending=1)
                BEGIN
                    INSERT ServiceAdministration.Sessions(Id,AccountId,SecurityVersion,TokenHash,CreatedAtUtc,LastSeenAtUtc,IdleExpiresAtUtc,AbsoluteExpiresAtUtc)
                        VALUES(@SessionId,@AccountId,@SecurityVersion,@TokenHash,@Now,@Now,@IdleExpiresAtUtc,@AbsoluteExpiresAtUtc);
                    SELECT @SessionId AS SessionId;
                END;
                COMMIT;
            END;
            """);
        migration.Sql("""
            CREATE PROCEDURE ServiceAdministration.ResolveSession
                @TokenHash varbinary(max),@Now datetimeoffset,@IdleTimeoutSeconds int
            AS
            BEGIN
                SET NOCOUNT ON; SET XACT_ABORT ON;
                DECLARE @Resolved TABLE(SessionId uniqueidentifier,AccountId uniqueidentifier,Email nvarchar(256));
                IF @TokenHash IS NOT NULL AND DATALENGTH(@TokenHash)=32 AND @Now IS NOT NULL
                    AND @IdleTimeoutSeconds BETWEEN 1 AND 2147483647
                BEGIN
                    BEGIN TRANSACTION;
                    DECLARE @AccountId uniqueidentifier,@Version bigint,@Email nvarchar(256);
                    SELECT @AccountId=AccountId FROM ServiceAdministration.Sessions WHERE TokenHash=@TokenHash;
                    SELECT @Version=SecurityVersion,@Email=Email FROM ServiceAdministration.Accounts WITH(UPDLOCK,HOLDLOCK)
                        WHERE Id=@AccountId AND IsEnabled=1;
                    UPDATE ServiceAdministration.Sessions WITH(UPDLOCK)
                        SET LastSeenAtUtc=CASE WHEN @Now>LastSeenAtUtc THEN @Now ELSE LastSeenAtUtc END,
                            IdleExpiresAtUtc=CASE
                                WHEN DATEDIFF_BIG(second,@Now,AbsoluteExpiresAtUtc)<=@IdleTimeoutSeconds THEN AbsoluteExpiresAtUtc
                                WHEN DATEADD(second,@IdleTimeoutSeconds,@Now)>IdleExpiresAtUtc THEN DATEADD(second,@IdleTimeoutSeconds,@Now)
                                ELSE IdleExpiresAtUtc END
                        OUTPUT inserted.Id,inserted.AccountId,@Email INTO @Resolved
                        WHERE TokenHash=@TokenHash AND AccountId=@AccountId AND SecurityVersion=@Version
                            AND RevokedAtUtc IS NULL AND IdleExpiresAtUtc>@Now AND AbsoluteExpiresAtUtc>@Now
                            AND NOT EXISTS(SELECT 1 FROM Security.WorkbenchRestorePending WHERE IsPending=1);
                    COMMIT;
                END;
                SELECT SessionId,AccountId,Email FROM @Resolved;
            END;
            """);
        migration.Sql("""
            CREATE PROCEDURE ServiceAdministration.RevokeSession
                @AccountId uniqueidentifier,@SessionId uniqueidentifier,@Now datetimeoffset
            AS
            BEGIN
                SET NOCOUNT ON;
                IF @Now IS NULL THROW 50040,'Invalid service-admin logout input.',1;
                UPDATE ServiceAdministration.Sessions SET RevokedAtUtc=@Now,RevocationReason=N'Logout'
                    WHERE Id=@SessionId AND AccountId=@AccountId AND RevokedAtUtc IS NULL;
            END;
            """);
        migration.Sql("""
            CREATE PROCEDURE ServiceAdministration.RehashPassword
                @AccountId uniqueidentifier,@SecurityVersion bigint,@PasswordHash nvarchar(max),@ExpectedPasswordHash nvarchar(max)
            AS
            BEGIN
                SET NOCOUNT ON;
                IF @PasswordHash IS NULL OR LEN(@PasswordHash)=0 OR DATALENGTH(@PasswordHash)>2048
                    THROW 50040,'Invalid service-admin password hash.',1;
                UPDATE ServiceAdministration.Accounts SET PasswordHash=@PasswordHash
                    WHERE Id=@AccountId AND SecurityVersion=@SecurityVersion AND IsEnabled=1
                        AND PasswordHash COLLATE Latin1_General_100_BIN2=@ExpectedPasswordHash COLLATE Latin1_General_100_BIN2
                        AND NOT EXISTS(SELECT 1 FROM Security.WorkbenchRestorePending WHERE IsPending=1);
                SELECT @@ROWCOUNT;
            END;
            """);
        foreach (var role in new[] { "workbench_web", "workbench_worker", "workbench_operator" })
            foreach (var table in new[] { "Accounts", "Sessions" })
                migration.Sql($"DENY SELECT,INSERT,UPDATE,DELETE ON ServiceAdministration.{table} TO {role};");
        foreach (var command in new[] { "FindAccountForLogin", "CreateSession", "ResolveSession", "RevokeSession", "RehashPassword" })
            migration.Sql($"GRANT EXECUTE ON ServiceAdministration.{command} TO workbench_web;");
        foreach (var command in new[] { "ProvisionServiceAdmin", "DisableServiceAdmin", "ResetServiceAdminPassword", "RevokeServiceAdminSessions" })
            migration.Sql($"GRANT EXECUTE ON Administration.{command} TO workbench_operator;");
        migration.Sql($"""
            DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Security.ReadDatabaseReadiness'));
            IF @Definition IS NULL OR CHARINDEX(N'20260928071548_AddSupplierOpenItems',@Definition)=0
                THROW 50020,'Unsupported service-admin readiness predecessor.',1;
            SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Definition=REPLACE(@Definition,N'20260928071548_AddSupplierOpenItems',N'{migrationId}');
            SET @Definition=REPLACE(@Definition,N'AS [SensitiveLimiterAvailable]',N'AS [SensitiveLimiterAvailable],
                CONVERT(bit,CASE WHEN
                    (SELECT COUNT(*) FROM sys.procedures WHERE schema_id=SCHEMA_ID(N''ServiceAdministration'')
                        AND name IN(N''FindAccountForLogin'',N''CreateSession'',N''ResolveSession'',N''RevokeSession'',N''RehashPassword''))=5
                    AND (SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id=DATABASE_PRINCIPAL_ID(N''workbench_web'')
                        AND major_id IN(OBJECT_ID(N''ServiceAdministration.FindAccountForLogin''),OBJECT_ID(N''ServiceAdministration.CreateSession''),OBJECT_ID(N''ServiceAdministration.ResolveSession''),OBJECT_ID(N''ServiceAdministration.RevokeSession''),OBJECT_ID(N''ServiceAdministration.RehashPassword''))
                        AND permission_name=N''EXECUTE'' AND state=N''G'')=5
                    AND (SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id IN(DATABASE_PRINCIPAL_ID(N''workbench_web''),DATABASE_PRINCIPAL_ID(N''workbench_worker''),DATABASE_PRINCIPAL_ID(N''workbench_operator''))
                        AND major_id IN(OBJECT_ID(N''ServiceAdministration.Accounts''),OBJECT_ID(N''ServiceAdministration.Sessions''))
                        AND permission_name IN(N''SELECT'',N''INSERT'',N''UPDATE'',N''DELETE'') AND state=N''D'')=24
                    AND (SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id=DATABASE_PRINCIPAL_ID(N''workbench_operator'')
                        AND major_id IN(OBJECT_ID(N''Administration.ProvisionServiceAdmin''),OBJECT_ID(N''Administration.DisableServiceAdmin''),OBJECT_ID(N''Administration.ResetServiceAdminPassword''),OBJECT_ID(N''Administration.RevokeServiceAdminSessions''))
                        AND permission_name=N''EXECUTE'' AND state=N''G'')=4
                    AND NOT EXISTS(SELECT 1 FROM sys.database_permissions WHERE permission_name=N''EXECUTE'' AND state IN(N''G'',N''W'')
                        AND ((grantee_principal_id IN(DATABASE_PRINCIPAL_ID(N''workbench_web''),DATABASE_PRINCIPAL_ID(N''workbench_worker''),DATABASE_PRINCIPAL_ID(N''public''))
                            AND major_id IN(OBJECT_ID(N''Administration.ProvisionServiceAdmin''),OBJECT_ID(N''Administration.DisableServiceAdmin''),OBJECT_ID(N''Administration.ResetServiceAdminPassword''),OBJECT_ID(N''Administration.RevokeServiceAdminSessions'')))
                        OR (grantee_principal_id IN(DATABASE_PRINCIPAL_ID(N''workbench_operator''),DATABASE_PRINCIPAL_ID(N''workbench_worker''),DATABASE_PRINCIPAL_ID(N''public''))
                            AND major_id IN(OBJECT_ID(N''ServiceAdministration.FindAccountForLogin''),OBJECT_ID(N''ServiceAdministration.CreateSession''),OBJECT_ID(N''ServiceAdministration.ResolveSession''),OBJECT_ID(N''ServiceAdministration.RevokeSession''),OBJECT_ID(N''ServiceAdministration.RehashPassword'')))))
                    THEN 1 ELSE 0 END) AS [ServiceAdminIdentityReady]');
            EXEC sys.sp_executesql @Definition;
            """);
    }
}
