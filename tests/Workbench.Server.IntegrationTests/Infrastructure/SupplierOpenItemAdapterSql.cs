// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.IntegrationTests.Infrastructure;

// Installed only in each disposable SQL clone. It exposes the participant to the
// restricted principal without granting production financial mutation authority.
internal static class SupplierOpenItemAdapterSql
{
    internal const string Install = """
        EXEC(N'CREATE PROCEDURE Purchasing.AppendFixtureSupplierEventGroup
          @ActorId uniqueidentifier,@SessionId uniqueidentifier,@Command nvarchar(max)
        WITH EXECUTE AS OWNER
        AS
        BEGIN
          SET XACT_ABORT ON; SET NOCOUNT ON;
          DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N''TenantId''));
          DECLARE @GroupId uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Command,''$.groupId'')),
            @RecordedAtUtc datetimeoffset=TRY_CONVERT(datetimeoffset,JSON_VALUE(@Command,''$.recordedAtUtc'')),
            @Events nvarchar(max)=JSON_QUERY(@Command,''$.events'');
          IF @TenantId IS NULL OR @GroupId IS NULL OR @RecordedAtUtc IS NULL
            THROW 51000,''Invalid fixture group command.'',1;
          BEGIN TRAN;
          DECLARE @LockResult int,@Resource nvarchar(255)=N''Accounting:''+CONVERT(nvarchar(36),@TenantId);
          EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,
            @LockMode=''Exclusive'',@LockOwner=''Transaction'',@LockTimeout=10000;
          IF @LockResult<0 THROW 51009,''Accounting is being changed.'',1;
          EXEC Accounting.RequirePermission @ActorId,@SessionId,N''SupplierBillsPost'';
          EXEC Purchasing.AppendSupplierEventGroup @TenantId,@GroupId,@RecordedAtUtc,@Events;
          COMMIT;
          SELECT (SELECT @GroupId groupId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER) ResultJson;
        END');
        GRANT EXECUTE ON Purchasing.AppendFixtureSupplierEventGroup TO workbench_web;
        -- A deliberately competing grant proves the migration's explicit DENY,
        -- rather than merely the role's default absence of a write grant.
        GRANT UPDATE ON Purchasing.SupplierItemMovements TO public;
        """;
}
