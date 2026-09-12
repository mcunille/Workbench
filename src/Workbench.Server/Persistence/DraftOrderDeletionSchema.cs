// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;
namespace Workbench.Server.Persistence;

internal static class DraftOrderDeletionSchema
{
    internal static void Apply(MigrationBuilder migrationBuilder, string migrationId)
    {
        // Preserve the original migration. Both visibility and the locked update recheck must exclude tombstones.
        migrationBuilder.Sql("""
            DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Purchasing].[SaveDraftOrder]'));
            DECLARE @Predicate nvarchar(max)=N'WHERE TenantId=@TenantId AND Id=@TargetId';
            IF @Definition IS NULL OR CHARINDEX(@Predicate,@Definition)=0
                OR CHARINDEX(N'WITH(UPDLOCK,HOLDLOCK)',@Definition)=0 OR CHARINDEX(N'CREATE PROCEDURE',@Definition)=0
                THROW 50020,'The expected purchasing save command is required for deletion support.',1;
            SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Definition=REPLACE(@Definition,@Predicate,@Predicate+N' AND IsDeleted=0');
            EXEC sys.sp_executesql @Definition;
            """);
        migrationBuilder.Sql(Delete);
        migrationBuilder.Sql($"""
            GRANT EXECUTE ON [Purchasing].[DeleteDraftOrder] TO [workbench_web];
            DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Readiness=REPLACE(@Readiness,N'20260912033355_TightenDraftSourceLinkValidation',N'{migrationId}');
            EXEC sys.sp_executesql @Readiness;
            """);
    }

    private const string Delete = """
        CREATE PROCEDURE [Purchasing].[DeleteDraftOrder]
            @RequestId uniqueidentifier,@ActorUserId uniqueidentifier,@DraftOrderId uniqueidentifier,@ExpectedRowVersion varbinary(max)
        AS
        BEGIN
            SET NOCOUNT ON; SET XACT_ABORT ON;
            DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'));
            IF @TenantId IS NULL OR NOT EXISTS(SELECT 1 FROM [Security].[fn_tenant_access](@TenantId))
                OR NOT EXISTS(SELECT 1 FROM [Identity].[Users] WHERE TenantId=@TenantId AND Id=@ActorUserId AND State=1)
                OR NOT EXISTS(SELECT 1 FROM [Tenancy].[Tenants] WHERE Id=@TenantId AND IsEnabled=1)
                THROW 50403,'Current tenant authority is required.',1;
            IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000'
                OR @DraftOrderId IS NULL OR @DraftOrderId='00000000-0000-0000-0000-000000000000'
                OR @ExpectedRowVersion IS NULL OR DATALENGTH(@ExpectedRowVersion)<>8
                THROW 50400,'A draft, request identifier and current version are required.',1;
            -- Deletion has no content body. Fingerprint V1 uses the same operation/target/version/draft envelope.
            DECLARE @EncodedVersion varchar(12)=CAST(N'' AS xml).value('xs:base64Binary(sql:variable("@ExpectedRowVersion"))','varchar(12)');
            DECLARE @Canonical nvarchar(max)=N'{"operation":"Delete","targetId":"'+LOWER(CONVERT(nvarchar(36),@DraftOrderId))
                +N'","expectedVersion":"'+CONVERT(nvarchar(12),@EncodedVersion)+N'","draft":null}';
            DECLARE @Fingerprint binary(32)=HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Canonical));
            BEGIN TRY
                BEGIN TRANSACTION;
                DECLARE @LockResult int,@Resource nvarchar(255)=N'Purchasing.DraftRequest:'+CONVERT(nvarchar(36),@TenantId)+N':'+CONVERT(nvarchar(36),@RequestId);
                EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
                IF @LockResult<0 THROW 50411,'The draft request could not acquire its save lock.',1;
                -- Retained tombstones are visible only for exact deletion receipt recovery; foreign targets remain 404.
                IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WHERE TenantId=@TenantId AND Id=@DraftOrderId)
                    THROW 50404,'Draft not found.',1;
                IF EXISTS(SELECT 1 FROM Purchasing.DraftOrderRequestReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId)
                BEGIN
                    IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrderRequestReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId
                        AND Operation='Delete' AND DraftOrderId=@DraftOrderId AND ActorUserId=@ActorUserId
                        AND ExpectedRowVersion=@ExpectedRowVersion AND FingerprintVersion=1 AND InputFingerprint=@Fingerprint)
                        THROW 50410,'This request identifier was used for different input.',1;
                    COMMIT;
                    SELECT RequestId,CONVERT(bit,1) Replayed,DraftOrderId,ResultRowVersion SavedVersion,CompletedAtUtc
                        FROM Purchasing.DraftOrderRequestReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId;
                    RETURN;
                END;
                DECLARE @CurrentVersion binary(8),@IsDeleted bit,@Created datetimeoffset(7),@Now datetimeoffset(7)=SYSUTCDATETIME();
                SELECT @CurrentVersion=RowVersion,@IsDeleted=IsDeleted,@Created=CreatedAtUtc
                    FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@DraftOrderId;
                IF @CurrentVersion IS NULL OR @IsDeleted=1 THROW 50404,'Draft not found.',1;
                IF @CurrentVersion<>@ExpectedRowVersion THROW 50409,'The draft changed. Review the current version.',1;
                IF @Now<@Created SET @Now=@Created;
                UPDATE Purchasing.DraftOrders SET IsDeleted=1,Title=NULL,SupplierName=NULL,Currency=NULL,Notes=NULL,
                    ContentSchemaVersion=1,ContentJson=N'{"sourceLinks":[],"entries":[]}',UpdatedAtUtc=@Now,UpdatedByUserId=@ActorUserId
                    WHERE TenantId=@TenantId AND Id=@DraftOrderId;
                SELECT @CurrentVersion=RowVersion FROM Purchasing.DraftOrders WHERE TenantId=@TenantId AND Id=@DraftOrderId;
                INSERT Purchasing.DraftOrderRequestReceipts(TenantId,RequestId,DraftOrderId,Operation,ActorUserId,ExpectedRowVersion,FingerprintVersion,InputFingerprint,ResultRowVersion,CompletedAtUtc)
                    VALUES(@TenantId,@RequestId,@DraftOrderId,'Delete',@ActorUserId,@ExpectedRowVersion,1,@Fingerprint,@CurrentVersion,@Now);
                COMMIT;
                SELECT @RequestId RequestId,CONVERT(bit,0) Replayed,@DraftOrderId DraftOrderId,@CurrentVersion SavedVersion,@Now CompletedAtUtc;
            END TRY
            BEGIN CATCH
                IF XACT_STATE()<>0 ROLLBACK;
                THROW;
            END CATCH;
        END;
        """;
}
