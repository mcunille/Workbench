// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;
namespace Workbench.Server.Persistence;

internal static partial class PurchasingIdentitySchema
{
    internal static void Protect(MigrationBuilder migrationBuilder, string migrationId)
    {
        foreach (var table in new[] { "Suppliers", "SupplierRequestReceipts", "PurchaseOrderCounters" })
            migrationBuilder.Sql($"""
                ALTER SECURITY POLICY [Security].[TenantIsolationPolicy]
                  ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Purchasing].[{table}],
                  ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Purchasing].[{table}] AFTER INSERT,
                  ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Purchasing].[{table}] AFTER UPDATE;
                GRANT SELECT ON [Purchasing].[{table}] TO [workbench_web];
                DENY INSERT,UPDATE,DELETE ON [Purchasing].[{table}] TO [workbench_web];
                """);
        migrationBuilder.Sql(ValidateContact);
        migrationBuilder.Sql(ValidateEmail);
        migrationBuilder.Sql(SaveSupplier);
        migrationBuilder.Sql(SaveDraftV2);
        migrationBuilder.Sql("GRANT EXECUTE ON [Purchasing].[SaveSupplier] TO [workbench_web];");
        foreach (var operation in new[] { "Create", "Update" })
        {
            migrationBuilder.Sql($"""
                CREATE PROCEDURE [Purchasing].[{operation}DraftOrderV2]
                  @RequestId uniqueidentifier,@CanonicalInputJson nvarchar(max),@ActorUserId uniqueidentifier AS
                BEGIN
                  SET NOCOUNT ON;
                  EXEC [Purchasing].[SaveDraftOrderV2] @RequestId,@CanonicalInputJson,@ActorUserId,'{operation}';
                END;
                """);
            migrationBuilder.Sql($"GRANT EXECUTE ON [Purchasing].[{operation}DraftOrderV2] TO [workbench_web];");
        }
        // Keep V1 canonicalization and receipt bytes intact; only matching pre-upgrade saves may run.
        migrationBuilder.Sql("""
            DECLARE @Old nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Purchasing].[SaveDraftOrder]'));
            SET @Old=REPLACE(@Old,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Old=REPLACE(@Old,N'DECLARE @Fields TABLE',N'THROW 50426,''Reload this draft with the current contract.'',1; DECLARE @Fields TABLE');
            EXEC sys.sp_executesql @Old;
            DECLARE @Delete nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Purchasing].[DeleteDraftOrder]'));
            SET @Delete=REPLACE(@Delete,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Delete=REPLACE(@Delete,N'SET IsDeleted=1,',N'SET IsDeleted=1,SupplierId=NULL,SupplierContactName=NULL,SupplierEmail=NULL,SupplierPhone=NULL,SupplierWebsite=NULL,SupplierPostalAddress=NULL,SupplierOrderReference=NULL,Platform=NULL,');
            EXEC sys.sp_executesql @Delete;
            """);
        migrationBuilder.Sql($"""
            DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Readiness=REPLACE(@Readiness,N'20260912030844_AddDraftSupplierOrders',N'{migrationId}');
            EXEC sys.sp_executesql @Readiness;
            """);
    }
    private const string ValidateContact = """
        CREATE FUNCTION [Purchasing].[IsSupplierText](@Value nvarchar(max),@Limit int,@Multiline bit) RETURNS bit AS
        BEGIN
            IF @Value IS NULL RETURN 1;
            IF DATALENGTH(@Value)>@Limit*2 RETURN 0;
            DECLARE @Whitespace nvarchar(64)=NCHAR(9)+NCHAR(10)+NCHAR(11)+NCHAR(12)+NCHAR(13)+NCHAR(32)+NCHAR(133)+NCHAR(160)+NCHAR(5760)+NCHAR(8192)+NCHAR(8193)+NCHAR(8194)+NCHAR(8195)+NCHAR(8196)+NCHAR(8197)+NCHAR(8198)+NCHAR(8199)+NCHAR(8200)+NCHAR(8201)+NCHAR(8202)+NCHAR(8232)+NCHAR(8233)+NCHAR(8239)+NCHAR(8287)+NCHAR(12288);
            IF DATALENGTH(TRIM(@Whitespace FROM @Value))=0 RETURN 0;
            IF @Multiline=1 RETURN 1;
            IF CONVERT(varbinary(max),@Value)<>CONVERT(varbinary(max),TRIM(@Whitespace FROM @Value)) RETURN 0;
            DECLARE @I int=1,@Code int;
            WHILE @I<=DATALENGTH(@Value)/2
            BEGIN
              SET @Code=UNICODE(SUBSTRING(@Value COLLATE Latin1_General_100_BIN2,@I,1));
              IF @Code BETWEEN 0 AND 31 OR @Code BETWEEN 127 AND 159 RETURN 0;
              SET @I+=1;
            END;
            RETURN 1;
        END;
        """;
    private const string ValidateEmail = """
        CREATE FUNCTION [Purchasing].[IsSupplierEmail](@Value nvarchar(max)) RETURNS bit AS
        BEGIN
            IF @Value IS NULL RETURN 1;
            IF Purchasing.IsSupplierText(@Value,254,0)=0 OR CHARINDEX(N'@',@Value)<=1 OR CHARINDEX(N'@',@Value)=LEN(@Value)
              OR CHARINDEX(N'@',@Value,CHARINDEX(N'@',@Value)+1)>0 OR @Value LIKE N'%[<>,;]%' RETURN 0;
            DECLARE @I int=1,@Code int;
            WHILE @I<=DATALENGTH(@Value)/2
            BEGIN
                SET @Code=UNICODE(SUBSTRING(@Value COLLATE Latin1_General_100_BIN2,@I,1));
                IF @Code BETWEEN 9 AND 13 OR @Code IN(32,133,160,5760,8232,8233,8239,8287,12288) OR @Code BETWEEN 8192 AND 8202 RETURN 0;
                SET @I+=1;
            END;
            RETURN 1;
        END;
        """;
    private const string SaveSupplier = """
        CREATE PROCEDURE [Purchasing].[SaveSupplier]
          @RequestId uniqueidentifier,@CanonicalInputJson nvarchar(max),@ActorUserId uniqueidentifier AS
        BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'));
          IF @TenantId IS NULL OR NOT EXISTS(SELECT 1 FROM Security.fn_tenant_access(@TenantId))
            OR NOT EXISTS(SELECT 1 FROM [Identity].[Users] WHERE TenantId=@TenantId AND Id=@ActorUserId AND State=1)
            OR NOT EXISTS(SELECT 1 FROM Tenancy.Tenants WHERE Id=@TenantId AND IsEnabled=1)
            THROW 50503,'Current tenant authority is required.',1;
          IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000'
            OR @CanonicalInputJson IS NULL OR DATALENGTH(@CanonicalInputJson)>131072 OR ISJSON(@CanonicalInputJson,OBJECT)<>1
            THROW 50500,'Review supplier fields.',1;
          DECLARE @Envelope TABLE([key] nvarchar(4000) COLLATE Latin1_General_100_BIN2,[value] nvarchar(max),[type] int);
          INSERT @Envelope SELECT [key],[value],[type] FROM OPENJSON(@CanonicalInputJson);
          IF (SELECT COUNT(*) FROM @Envelope)<>5 OR EXISTS(SELECT [key] FROM @Envelope GROUP BY [key] HAVING COUNT(*)<>1)
            OR EXISTS(SELECT 1 FROM @Envelope WHERE CONVERT(varbinary(max),[key]) NOT IN(CONVERT(varbinary(max),N'operation'),CONVERT(varbinary(max),N'targetId'),CONVERT(varbinary(max),N'expectedVersion'),CONVERT(varbinary(max),N'supplier'),CONVERT(varbinary(max),N'isArchived')))
            THROW 50500,'Review supplier fields.',1;
          DECLARE @Operation nvarchar(max)=JSON_VALUE(@CanonicalInputJson,'$.operation'),@TargetText nvarchar(max)=JSON_VALUE(@CanonicalInputJson,'$.targetId'),
            @EncodedVersion nvarchar(max)=JSON_VALUE(@CanonicalInputJson,'$.expectedVersion'),@ExpectedVersion varbinary(max),@TargetId uniqueidentifier,
            @IsArchived bit=CASE JSON_VALUE(@CanonicalInputJson,'$.isArchived') WHEN 'true' THEN 1 WHEN 'false' THEN 0 END;
          IF NOT EXISTS(SELECT 1 FROM @Envelope WHERE [key]=N'operation' AND [type]=1 AND CONVERT(varbinary(max),[value]) IN(CONVERT(varbinary(max),N'Create'),CONVERT(varbinary(max),N'Update'),CONVERT(varbinary(max),N'Archive')))
            THROW 50500,'Review supplier fields.',1;
          IF @Operation='Create' AND EXISTS(SELECT 1 FROM @Envelope WHERE [key] IN(N'targetId',N'expectedVersion') AND [type]<>0) THROW 50500,'Review supplier fields.',1;
          IF (@Operation='Archive' AND (NOT EXISTS(SELECT 1 FROM @Envelope WHERE [key]=N'isArchived' AND [type]=3) OR NOT EXISTS(SELECT 1 FROM @Envelope WHERE [key]=N'supplier' AND [type]=0)))
            OR (@Operation<>'Archive' AND (NOT EXISTS(SELECT 1 FROM @Envelope WHERE [key]=N'isArchived' AND [type]=0) OR NOT EXISTS(SELECT 1 FROM @Envelope WHERE [key]=N'supplier' AND [type]=5)))
            THROW 50500,'Review supplier fields.',1;
          IF @Operation<>'Create'
          BEGIN
            SET @TargetId=TRY_CONVERT(uniqueidentifier,@TargetText);
            IF @TargetId IS NULL OR @TargetId='00000000-0000-0000-0000-000000000000' OR DATALENGTH(@TargetText)<>72 OR DATALENGTH(@EncodedVersion)<>24
              OR EXISTS(SELECT 1 FROM @Envelope WHERE [key] IN(N'targetId',N'expectedVersion') AND [type]<>1) THROW 50500,'Review supplier fields.',1;
            BEGIN TRY
              SET @ExpectedVersion=CAST(N'' AS xml).value('xs:base64Binary(sql:variable("@EncodedVersion"))','varbinary(max)');
            END TRY BEGIN CATCH THROW 50500,'Review supplier fields.',1; END CATCH;
            IF DATALENGTH(@ExpectedVersion)<>8 THROW 50500,'Review supplier fields.',1;
          END;
          DECLARE @Fingerprint binary(32)=HASHBYTES('SHA2_256',CONVERT(varbinary(max),@CanonicalInputJson));
          BEGIN TRY
            BEGIN TRANSACTION;
            DECLARE @LockResult int,@Resource nvarchar(255)=N'Purchasing.SupplierRequest:'+CONVERT(nvarchar(36),@TenantId)+N':'+CONVERT(nvarchar(36),@RequestId);
            EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
            IF @LockResult<0 THROW 50511,'Supplier request lock unavailable.',1;
            IF @Operation<>'Create' AND NOT EXISTS(SELECT 1 FROM Purchasing.Suppliers WHERE TenantId=@TenantId AND Id=@TargetId) THROW 50504,'Supplier not found.',1;
            IF EXISTS(SELECT 1 FROM Purchasing.SupplierRequestReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId)
            BEGIN
              -- Current tenant/actor authority was checked above. As with draft saves, an authorized
              -- colleague may recover the same business request; the original receipt actor stays immutable.
              IF NOT EXISTS(SELECT 1 FROM Purchasing.SupplierRequestReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId AND Operation=@Operation AND InputFingerprint=@Fingerprint)
                THROW 50510,'This request identifier was used for different input.',1;
              COMMIT;
              SELECT RequestId,CONVERT(bit,1) Replayed,SupplierId,ResultRowVersion SavedVersion,CompletedAtUtc FROM Purchasing.SupplierRequestReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId;
              RETURN;
            END;
            DECLARE @Name nvarchar(max),@ContactName nvarchar(max),@Email nvarchar(max),@Phone nvarchar(max),@Website nvarchar(max),@PostalAddress nvarchar(max);
            IF @Operation<>'Archive'
            BEGIN
              DECLARE @Fields TABLE([key] nvarchar(4000) COLLATE Latin1_General_100_BIN2,[value] nvarchar(max),[type] int);
              INSERT @Fields SELECT [key],[value],[type] FROM OPENJSON(JSON_QUERY(@CanonicalInputJson,'$.supplier'));
              IF (SELECT COUNT(*) FROM @Fields)<>6 OR EXISTS(SELECT [key] FROM @Fields GROUP BY [key] HAVING COUNT(*)<>1)
                OR EXISTS(SELECT 1 FROM @Fields WHERE [type] NOT IN(0,1) OR CONVERT(varbinary(max),[key]) NOT IN(CONVERT(varbinary(max),N'name'),CONVERT(varbinary(max),N'contactName'),CONVERT(varbinary(max),N'email'),CONVERT(varbinary(max),N'phone'),CONVERT(varbinary(max),N'website'),CONVERT(varbinary(max),N'postalAddress')))
                THROW 50500,'Review supplier fields.',1;
              SELECT @Name=[value] FROM @Fields WHERE [key]=N'name';
              SELECT @ContactName=[value] FROM @Fields WHERE [key]=N'contactName';
              SELECT @Email=[value] FROM @Fields WHERE [key]=N'email';
              SELECT @Phone=[value] FROM @Fields WHERE [key]=N'phone';
              SELECT @Website=[value] FROM @Fields WHERE [key]=N'website';
              SELECT @PostalAddress=[value] FROM @Fields WHERE [key]=N'postalAddress';
              IF @Name IS NULL OR Purchasing.IsSupplierText(@Name,200,0)=0 OR Purchasing.IsSupplierText(@ContactName,200,0)=0
                OR Purchasing.IsSupplierText(@Email,254,0)=0 OR Purchasing.IsSupplierText(@Phone,100,0)=0 OR Purchasing.IsSupplierText(@PostalAddress,2000,1)=0
                OR Purchasing.IsDraftSourceLink(@Website)=0
                OR Purchasing.IsSupplierEmail(@Email)=0
                THROW 50500,'Review supplier fields.',1;
            END;
            DECLARE @CurrentVersion binary(8),@Created datetimeoffset(7),@Now datetimeoffset(7)=SYSUTCDATETIME();
            IF @Operation<>'Create'
            BEGIN
              SELECT @CurrentVersion=RowVersion,@Created=CreatedAtUtc FROM Purchasing.Suppliers WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@TargetId;
              IF @CurrentVersion IS NULL THROW 50504,'Supplier not found.',1;
              IF @CurrentVersion<>@ExpectedVersion THROW 50509,'Supplier changed.',1;
              IF @Now<@Created SET @Now=@Created;
              IF @Operation='Archive'
                UPDATE Purchasing.Suppliers SET IsArchived=@IsArchived,UpdatedAtUtc=@Now,UpdatedByUserId=@ActorUserId WHERE TenantId=@TenantId AND Id=@TargetId;
              ELSE
                UPDATE Purchasing.Suppliers SET Name=@Name,ContactName=@ContactName,Email=@Email,Phone=@Phone,Website=@Website,PostalAddress=@PostalAddress,UpdatedAtUtc=@Now,UpdatedByUserId=@ActorUserId WHERE TenantId=@TenantId AND Id=@TargetId;
            END
            ELSE
            BEGIN
              SET @TargetId=NEWID();
              INSERT Purchasing.Suppliers(Id,TenantId,Name,ContactName,Email,Phone,Website,PostalAddress,IsArchived,CreatedAtUtc,UpdatedAtUtc,CreatedByUserId,UpdatedByUserId)
                VALUES(@TargetId,@TenantId,@Name,@ContactName,@Email,@Phone,@Website,@PostalAddress,0,@Now,@Now,@ActorUserId,@ActorUserId);
            END;
            SELECT @CurrentVersion=RowVersion FROM Purchasing.Suppliers WHERE TenantId=@TenantId AND Id=@TargetId;
            INSERT Purchasing.SupplierRequestReceipts(TenantId,RequestId,SupplierId,Operation,ActorUserId,ExpectedRowVersion,InputFingerprint,ResultRowVersion,CompletedAtUtc)
              VALUES(@TenantId,@RequestId,@TargetId,@Operation,@ActorUserId,@ExpectedVersion,@Fingerprint,@CurrentVersion,@Now);
            COMMIT;
            SELECT @RequestId RequestId,CONVERT(bit,0) Replayed,@TargetId SupplierId,@CurrentVersion SavedVersion,@Now CompletedAtUtc;
          END TRY BEGIN CATCH
            IF XACT_STATE()<>0 ROLLBACK;
            THROW;
          END CATCH;
        END;
        """;
}
