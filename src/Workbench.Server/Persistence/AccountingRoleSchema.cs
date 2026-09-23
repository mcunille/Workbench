// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;
namespace Workbench.Server.Persistence;

internal static class AccountingRoleSchema
{
    public static void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE Administration.AccountingRoles(
                TenantId uniqueidentifier NOT NULL,RoleId uniqueidentifier NOT NULL,Kind varchar(20) NOT NULL,
                CONSTRAINT PK_AccountingRoles PRIMARY KEY(TenantId,RoleId),
                CONSTRAINT UQ_AccountingRoleKind UNIQUE(TenantId,Kind),
                CONSTRAINT CK_AccountingRoleKind CHECK(Kind IN('Administrator','Reader')),
                CONSTRAINT FK_AccountingRoleIdentity FOREIGN KEY(TenantId,RoleId) REFERENCES [Identity].Roles(TenantId,Id));
            CREATE TABLE Administration.AccountingRoleAssignments(
                TenantId uniqueidentifier NOT NULL,UserId uniqueidentifier NOT NULL,Version uniqueidentifier NOT NULL,
                CONSTRAINT PK_AccountingRoleAssignments PRIMARY KEY(TenantId,UserId),
                CONSTRAINT FK_AccountingRoleAssignmentUser FOREIGN KEY(TenantId,UserId) REFERENCES [Identity].Users(TenantId,Id));
            CREATE TABLE Administration.AccountingRoleReceipts(
                TenantId uniqueidentifier NOT NULL,RequestId uniqueidentifier NOT NULL,UserId uniqueidentifier NOT NULL,
                ActorId uniqueidentifier NOT NULL,ExpectedVersion uniqueidentifier NOT NULL,Version uniqueidentifier NOT NULL,
                RoleIds nvarchar(100) NOT NULL,RecordedAtUtc datetimeoffset NOT NULL,
                CONSTRAINT PK_AccountingRoleReceipts PRIMARY KEY(TenantId,RequestId),
                CONSTRAINT FK_AccountingRoleReceiptUser FOREIGN KEY(TenantId,UserId) REFERENCES [Identity].Users(TenantId,Id),
                CONSTRAINT FK_AccountingRoleReceiptActor FOREIGN KEY(TenantId,ActorId) REFERENCES [Identity].Users(TenantId,Id));
            """);
        foreach (var table in new[] { "AccountingRoles", "AccountingRoleAssignments", "AccountingRoleReceipts" })
            migrationBuilder.Sql($"""
                CREATE SECURITY POLICY Administration.{table}TenantPolicy
                ADD FILTER PREDICATE Security.fn_tenant_access(TenantId) ON Administration.{table},
                ADD BLOCK PREDICATE Security.fn_tenant_access(TenantId) ON Administration.{table} AFTER INSERT,
                ADD BLOCK PREDICATE Security.fn_tenant_access(TenantId) ON Administration.{table} AFTER UPDATE WITH(STATE=ON);
                GRANT SELECT ON Administration.{table} TO workbench_web;
                DENY INSERT,UPDATE,DELETE ON Administration.{table} TO workbench_web;
                """);
        migrationBuilder.Sql(ProvisionRoles);
        migrationBuilder.Sql("""
            DECLARE @TenantId uniqueidentifier;
            DECLARE tenants CURSOR LOCAL FAST_FORWARD FOR SELECT Id FROM Tenancy.Tenants;
            OPEN tenants; FETCH NEXT FROM tenants INTO @TenantId;
            WHILE @@FETCH_STATUS=0 BEGIN
                EXEC Administration.CreateAccountingRoles @TenantId;
                FETCH NEXT FROM tenants INTO @TenantId;
            END;
            CLOSE tenants; DEALLOCATE tenants;
            DECLARE @Provision nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Administration.ProvisionTenant'));
            IF @Provision NOT LIKE N'%COMMIT TRANSACTION;%' THROW 50900,'Tenant provisioning definition changed.',1;
            SET @Provision=REPLACE(@Provision,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Provision=REPLACE(@Provision,N'COMMIT TRANSACTION;',N'EXEC Administration.CreateAccountingRoles @TenantId; COMMIT TRANSACTION;');
            EXEC sys.sp_executesql @Provision;
            DECLARE @Resolve nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Identity].ResolveSession'));
            DECLARE @Marker nvarchar(max)=N'FROM [Identity].[UserClaims] AS [claim]';
            IF CHARINDEX(@Marker,@Resolve)=0 THROW 50900,'Session resolver definition changed.',1;
            SET @Resolve=REPLACE(@Resolve,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Resolve=REPLACE(@Resolve,@Marker,N'FROM (SELECT * FROM [Identity].[UserClaims] WHERE ClaimValue NOT LIKE N''Accounting%'') AS [claim]');
            EXEC sys.sp_executesql @Resolve;
            DENY INSERT,UPDATE,DELETE ON [Identity].Roles TO workbench_web;
            DENY INSERT,UPDATE,DELETE ON [Identity].RoleClaims TO workbench_web;
            DENY INSERT,UPDATE,DELETE ON [Identity].UserRoles TO workbench_web;
            DENY INSERT,UPDATE,DELETE ON [Identity].UserClaims TO workbench_web;
            """);
        migrationBuilder.Sql(RequirePermission);
        migrationBuilder.Sql(AssignRoles);
        migrationBuilder.Sql("GRANT EXECUTE ON Administration.AssignAccountingRoles TO workbench_web;");
    }

    private const string ProvisionRoles = """
        CREATE PROCEDURE Administration.CreateAccountingRoles @TenantId uniqueidentifier
        WITH EXECUTE AS OWNER AS
        BEGIN
            SET NOCOUNT ON;
            DECLARE @Administrator uniqueidentifier=NEWID(),@Reader uniqueidentifier=NEWID();
            INSERT [Identity].Roles(Id,TenantId,Name,NormalizedName,ConcurrencyStamp) VALUES
                (@Administrator,@TenantId,N'Accounting administrator',N'ACCOUNTING ADMINISTRATOR',NEWID()),
                (@Reader,@TenantId,N'Accounting reader',N'ACCOUNTING READER',NEWID());
            INSERT Administration.AccountingRoles(TenantId,RoleId,Kind) VALUES
                (@TenantId,@Administrator,'Administrator'),(@TenantId,@Reader,'Reader');
            INSERT [Identity].RoleClaims(TenantId,RoleId,ClaimType,ClaimValue)
            SELECT @TenantId,@Administrator,N'workbench/permission',p FROM (VALUES
                (N'AccountingConfigurationRead'),(N'AccountingConfigurationManage'),(N'AccountingReportsRead'),
                (N'AccountingReportsExport'),(N'AccountingReconcile'),(N'AccountingPeriodsClose'),(N'AccountingFiscalYearsClose')) v(p);
            INSERT [Identity].RoleClaims(TenantId,RoleId,ClaimType,ClaimValue) VALUES
                (@TenantId,@Reader,N'workbench/permission',N'AccountingReportsRead'),
                (@TenantId,@Reader,N'workbench/permission',N'AccountingReportsExport');
        END;
        """;

    private const string RequirePermission = """
        CREATE PROCEDURE Accounting.RequirePermission
            @ActorId uniqueidentifier,@SessionId uniqueidentifier,@Permission nvarchar(100)
        AS BEGIN
            SET NOCOUNT ON;
            DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'));
            IF @TenantId IS NULL OR NOT EXISTS(SELECT 1 FROM Security.fn_tenant_access(@TenantId))
                THROW 50903,'Current tenant authority is required.',1;
            -- HOLDLOCK retains the authority until the enclosing command commits, including admitted requests.
            IF NOT EXISTS(SELECT 1 FROM [Identity].Users u WITH(HOLDLOCK)
                JOIN [Identity].Sessions s WITH(HOLDLOCK) ON s.UserId=u.Id AND s.TenantId=u.TenantId
                JOIN Tenancy.Tenants t WITH(HOLDLOCK) ON t.Id=u.TenantId
                WHERE u.TenantId=@TenantId AND u.Id=@ActorId AND u.State=1 AND t.IsEnabled=1
                AND s.Id=@SessionId AND s.RevokedAtUtc IS NULL AND s.SecurityVersion=u.SecurityVersion
                AND s.IdleExpiresAtUtc>SYSUTCDATETIME() AND s.AbsoluteExpiresAtUtc>SYSUTCDATETIME())
                THROW 50903,'Current session authority is required.',1;
            IF NOT EXISTS(SELECT 1 FROM [Identity].UserRoles m WITH(HOLDLOCK)
                JOIN [Identity].RoleClaims c WITH(HOLDLOCK) ON c.TenantId=m.TenantId AND c.RoleId=m.RoleId
                WHERE m.TenantId=@TenantId AND m.UserId=@ActorId AND c.ClaimType=N'workbench/permission' AND c.ClaimValue=@Permission)
                THROW 50903,'Current role permission is required.',1;
        END;
        """;

    private const string AssignRoles = """
        CREATE PROCEDURE Administration.AssignAccountingRoles
            @ActorId uniqueidentifier,@SessionId uniqueidentifier,@UserId uniqueidentifier,
            @RequestId uniqueidentifier,@ExpectedVersion uniqueidentifier,@RoleIds nvarchar(max)
        AS BEGIN
            SET NOCOUNT ON; SET XACT_ABORT ON;
            DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'));
            IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000' OR @ExpectedVersion IS NULL
                OR @RoleIds IS NULL OR DATALENGTH(@RoleIds)>200 OR ISJSON(@RoleIds,ARRAY)<>1
                THROW 50900,'Review the role assignment.',1;
            BEGIN TRY
                BEGIN TRANSACTION;
                DECLARE @LockResult int,@Resource nvarchar(255)=N'Accounting.Roles:'+LOWER(CONVERT(nvarchar(36),@TenantId));
                EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
                IF @LockResult<0 THROW 50909,'Role assignment is busy.',1;
                EXEC Accounting.RequirePermission @ActorId,@SessionId,N'TenantUsersManage';
                IF NOT EXISTS(SELECT 1 FROM [Identity].Users WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@UserId)
                    THROW 50904,'User not found.',1;
                IF NOT EXISTS(SELECT 1 FROM [Identity].Users WHERE TenantId=@TenantId AND Id=@UserId AND State=1)
                    THROW 50900,'Only enabled users can receive accounting roles.',1;
                IF (SELECT COUNT(*) FROM OPENJSON(@RoleIds))>2 OR EXISTS(
                    SELECT 1 FROM OPENJSON(@RoleIds) j WHERE j.type<>1 OR DATALENGTH(j.value)<>72 OR NOT EXISTS(
                        SELECT 1 FROM Administration.AccountingRoles r WHERE r.TenantId=@TenantId AND r.RoleId=TRY_CONVERT(uniqueidentifier,j.value)))
                    OR EXISTS(SELECT value FROM OPENJSON(@RoleIds) GROUP BY value HAVING COUNT(*)>1)
                    THROW 50900,'Select only the fixed accounting roles.',1;
                IF EXISTS(SELECT 1 FROM Administration.AccountingRoleReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId)
                BEGIN
                    IF NOT EXISTS(SELECT 1 FROM Administration.AccountingRoleReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId
                        AND UserId=@UserId AND ActorId=@ActorId AND ExpectedVersion=@ExpectedVersion AND RoleIds=@RoleIds)
                        THROW 50909,'Request ID was reused with changed input.',1;
                    COMMIT;
                    SELECT Version,RoleIds FROM Administration.AccountingRoleReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId;
                    RETURN;
                END;
                DECLARE @Current uniqueidentifier=COALESCE((SELECT Version FROM Administration.AccountingRoleAssignments WITH(UPDLOCK,HOLDLOCK)
                    WHERE TenantId=@TenantId AND UserId=@UserId),'00000000-0000-0000-0000-000000000000');
                IF @Current<>@ExpectedVersion THROW 50909,'Role membership changed.',1;
                DECLARE @Version uniqueidentifier=NEWID(),@Now datetimeoffset=SYSUTCDATETIME();
                DELETE m FROM [Identity].UserRoles m JOIN Administration.AccountingRoles r ON r.TenantId=m.TenantId AND r.RoleId=m.RoleId
                    WHERE m.TenantId=@TenantId AND m.UserId=@UserId;
                INSERT [Identity].UserRoles(TenantId,UserId,RoleId)
                    SELECT @TenantId,@UserId,CONVERT(uniqueidentifier,value) FROM OPENJSON(@RoleIds);
                IF @Current='00000000-0000-0000-0000-000000000000'
                    INSERT Administration.AccountingRoleAssignments VALUES(@TenantId,@UserId,@Version);
                ELSE UPDATE Administration.AccountingRoleAssignments SET Version=@Version WHERE TenantId=@TenantId AND UserId=@UserId;
                INSERT Administration.AccountingRoleReceipts VALUES(@TenantId,@RequestId,@UserId,@ActorId,@ExpectedVersion,@Version,@RoleIds,@Now);
                INSERT Security.TenantSecurityAuditEvents(Id,TenantId,ActorUserId,Action,TargetType,TargetId,Outcome,CorrelationId,MetadataJson,OccurredAtUtc)
                    VALUES(NEWID(),@TenantId,@ActorId,N'accounting.roles.assigned',N'User',@UserId,N'Succeeded',NULL,
                        N'{"requestId":"'+CONVERT(nvarchar(36),@RequestId)+N'"}',@Now);
                COMMIT;
                SELECT @Version Version,@RoleIds RoleIds;
            END TRY BEGIN CATCH
                IF XACT_STATE()<>0 ROLLBACK;
                THROW;
            END CATCH;
        END;
        """;
}
