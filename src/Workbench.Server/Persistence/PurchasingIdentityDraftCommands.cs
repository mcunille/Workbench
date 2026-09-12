// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Persistence;

internal static partial class PurchasingIdentitySchema
{
    private const string SaveDraftV2 = """
        CREATE PROCEDURE [Purchasing].[SaveDraftOrderV2]
            @RequestId uniqueidentifier,@CanonicalInputJson nvarchar(max),@ActorUserId uniqueidentifier,@Operation varchar(6)
        AS
        BEGIN
            SET NOCOUNT ON; SET XACT_ABORT ON;
            DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'));
            IF @TenantId IS NULL OR NOT EXISTS(SELECT 1 FROM [Security].[fn_tenant_access](@TenantId))
                OR NOT EXISTS(SELECT 1 FROM [Identity].[Users] WHERE TenantId=@TenantId AND Id=@ActorUserId AND State=1)
                OR NOT EXISTS(SELECT 1 FROM [Tenancy].[Tenants] WHERE Id=@TenantId AND IsEnabled=1)
                THROW 50403,'Current tenant authority is required.',1;
            IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000'
                OR @CanonicalInputJson IS NULL OR DATALENGTH(@CanonicalInputJson)>8388608 OR ISJSON(@CanonicalInputJson,OBJECT)<>1
                THROW 50400,'Review the draft fields.',1;
            DECLARE @Envelope TABLE([key] nvarchar(4000) COLLATE Latin1_General_100_BIN2,[value] nvarchar(max),[type] int);
            INSERT @Envelope SELECT [key],[value],[type] FROM OPENJSON(@CanonicalInputJson);
            -- Even BIN2 string equality pads trailing spaces. Check allowed property names as exact UTF-16 bytes.
            IF (SELECT COUNT(*) FROM @Envelope)<>4 OR EXISTS(SELECT [key] FROM @Envelope GROUP BY [key] HAVING COUNT(*)<>1)
                OR EXISTS(SELECT 1 FROM @Envelope WHERE CONVERT(varbinary(max),[key]) NOT IN(
                    CONVERT(varbinary(max),N'operation'),CONVERT(varbinary(max),N'targetId'),CONVERT(varbinary(max),N'expectedVersion'),CONVERT(varbinary(max),N'draft')))
                OR NOT EXISTS(SELECT 1 FROM @Envelope WHERE [key]=N'operation' AND [type]=1 AND CONVERT(varbinary(max),[value])=CONVERT(varbinary(max),CONVERT(nvarchar(6),@Operation)))
                OR NOT EXISTS(SELECT 1 FROM @Envelope WHERE [key]=N'draft' AND [type]=5)
                THROW 50400,'Review the draft fields.',1;
            DECLARE @TargetId uniqueidentifier,@ExpectedVersion varbinary(max),@EncodedVersion nvarchar(max),@Draft nvarchar(max),@TargetText nvarchar(max);
            SELECT @Draft=[value] FROM @Envelope WHERE [key]=N'draft';
            SELECT @TargetText=[value] FROM @Envelope WHERE [key]=N'targetId';
            SELECT @EncodedVersion=[value] FROM @Envelope WHERE [key]=N'expectedVersion';
            IF @Operation='Create' AND EXISTS(SELECT 1 FROM @Envelope WHERE [key] IN(N'targetId',N'expectedVersion') AND [type]<>0)
                THROW 50400,'Review the draft fields.',1;
            IF @Operation='Update'
            BEGIN
                SET @TargetId=TRY_CONVERT(uniqueidentifier,@TargetText);
                IF @TargetId IS NULL OR @TargetId='00000000-0000-0000-0000-000000000000' OR DATALENGTH(@TargetText)<>72
                    OR EXISTS(SELECT 1 FROM @Envelope WHERE [key] IN(N'targetId',N'expectedVersion') AND [type]<>1)
                    OR DATALENGTH(@EncodedVersion)<>24
                    THROW 50400,'Review the draft fields.',1;
                BEGIN TRY
                    SET @ExpectedVersion=CAST(N'' AS xml).value('xs:base64Binary(sql:variable("@EncodedVersion"))','varbinary(max)');
                END TRY BEGIN CATCH
                    THROW 50400,'Review the draft fields.',1;
                END CATCH;
                IF DATALENGTH(@ExpectedVersion)<>8 THROW 50400,'Review the draft fields.',1;
            END;
            DECLARE @Fingerprint binary(32)=HASHBYTES('SHA2_256',CONVERT(varbinary(max),@CanonicalInputJson));
            BEGIN TRY
                BEGIN TRANSACTION;
                DECLARE @LockResult int,@Resource nvarchar(255)=N'Purchasing.DraftRequest:'+CONVERT(nvarchar(36),@TenantId)+N':'+CONVERT(nvarchar(36),@RequestId);
                EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
                IF @LockResult<0 THROW 50411,'The draft request could not acquire its save lock.',1;
                -- A foreign target remains indistinguishable from a nonexistent draft, including conflicts.
                IF @Operation='Update' AND NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WHERE TenantId=@TenantId AND Id=@TargetId AND IsDeleted=0)
                    THROW 50404,'Draft not found.',1;
                IF EXISTS(SELECT 1 FROM Purchasing.DraftOrderRequestReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId)
                BEGIN
                    IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrderRequestReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId
                        AND Operation=@Operation AND FingerprintVersion=2 AND InputFingerprint=@Fingerprint
                        AND (@Operation='Create' OR (DraftOrderId=@TargetId AND ExpectedRowVersion=@ExpectedVersion)))
                        THROW 50410,'This request identifier was used for different input.',1;
                    COMMIT;
                    SELECT RequestId,CONVERT(bit,1) Replayed,DraftOrderId,ResultRowVersion SavedVersion,CompletedAtUtc
                        FROM Purchasing.DraftOrderRequestReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId;
                    RETURN;
                END;
                DECLARE @Fields TABLE([key] nvarchar(4000) COLLATE Latin1_General_100_BIN2,[value] nvarchar(max),[type] int);
                INSERT @Fields SELECT [key],[value],[type] FROM OPENJSON(@Draft);
                IF (SELECT COUNT(*) FROM @Fields)<>14 OR EXISTS(SELECT [key] FROM @Fields GROUP BY [key] HAVING COUNT(*)<>1)
                    OR EXISTS(SELECT 1 FROM @Fields WHERE CONVERT(varbinary(max),[key]) NOT IN(
                        CONVERT(varbinary(max),N'title'),CONVERT(varbinary(max),N'supplierName'),CONVERT(varbinary(max),N'currency'),
                        CONVERT(varbinary(max),N'notes'),CONVERT(varbinary(max),N'sourceLinks'),CONVERT(varbinary(max),N'entries'),
                        CONVERT(varbinary(max),N'supplierId'),CONVERT(varbinary(max),N'supplierContactName'),CONVERT(varbinary(max),N'supplierEmail'),CONVERT(varbinary(max),N'supplierPhone'),CONVERT(varbinary(max),N'supplierWebsite'),CONVERT(varbinary(max),N'supplierPostalAddress'),CONVERT(varbinary(max),N'supplierOrderReference'),CONVERT(varbinary(max),N'platform')))
                    OR EXISTS(SELECT 1 FROM @Fields WHERE ([key] IN(N'sourceLinks',N'entries') AND [type]<>4) OR ([key] NOT IN(N'sourceLinks',N'entries') AND [type] NOT IN(0,1)))
                    THROW 50400,'Review the draft fields.',1;
                DECLARE @Title nvarchar(max),@Supplier nvarchar(max),@Currency nvarchar(max),@Notes nvarchar(max),@Links nvarchar(max),@Entries nvarchar(max);
                SELECT @Title=[value] FROM @Fields WHERE [key]=N'title';
                SELECT @Supplier=[value] FROM @Fields WHERE [key]=N'supplierName';
                SELECT @Currency=[value] FROM @Fields WHERE [key]=N'currency';
                SELECT @Notes=[value] FROM @Fields WHERE [key]=N'notes';
                SELECT @Links=JSON_QUERY(@Draft,'$.sourceLinks'),@Entries=JSON_QUERY(@Draft,'$.entries');
                DECLARE @Whitespace nvarchar(64)=NCHAR(9)+NCHAR(10)+NCHAR(11)+NCHAR(12)+NCHAR(13)+NCHAR(32)+NCHAR(133)+NCHAR(160)+NCHAR(5760)+NCHAR(8192)+NCHAR(8193)+NCHAR(8194)+NCHAR(8195)+NCHAR(8196)+NCHAR(8197)+NCHAR(8198)+NCHAR(8199)+NCHAR(8200)+NCHAR(8201)+NCHAR(8202)+NCHAR(8232)+NCHAR(8233)+NCHAR(8239)+NCHAR(8287)+NCHAR(12288);
                IF DATALENGTH(@Title)>400 OR DATALENGTH(@Supplier)>400 OR DATALENGTH(@Notes)>20000
                    OR EXISTS(SELECT 1 FROM @Fields WHERE [key] IN(N'title',N'supplierName') AND [type]=1
                        AND (DATALENGTH(TRIM(@Whitespace FROM [value]))=0 OR CONVERT(varbinary(max),[value])<>CONVERT(varbinary(max),TRIM(@Whitespace FROM [value]))))
                    OR (@Notes IS NOT NULL AND DATALENGTH(TRIM(@Whitespace FROM @Notes))=0)
                    OR (@Currency IS NOT NULL AND (DATALENGTH(@Currency)<>6 OR @Currency COLLATE Latin1_General_100_BIN2 LIKE N'%[^A-Z]%'))
                    OR (SELECT COUNT(*) FROM OPENJSON(@Links))>20 OR (SELECT COUNT(*) FROM OPENJSON(@Entries))>100
                    OR EXISTS(SELECT 1 FROM OPENJSON(@Links) WHERE [type]<>1 OR Purchasing.IsDraftSourceLink([value])<>1)
                    OR EXISTS(SELECT 1 FROM OPENJSON(@Entries) WHERE [type]<>5)
                    THROW 50400,'Review the draft fields.',1;
                DECLARE @EntryFields TABLE(EntryIndex int,[key] nvarchar(4000) COLLATE Latin1_General_100_BIN2,[value] nvarchar(max),[type] int);
                INSERT @EntryFields SELECT CONVERT(int,e.[key]),p.[key],p.[value],p.[type] FROM OPENJSON(@Entries) e CROSS APPLY OPENJSON(e.[value]) p;
                IF EXISTS(SELECT 1 FROM OPENJSON(@Entries) e WHERE (SELECT COUNT(*) FROM @EntryFields p WHERE p.EntryIndex=CONVERT(int,e.[key]))<>5)
                    OR EXISTS(SELECT EntryIndex,[key] FROM @EntryFields GROUP BY EntryIndex,[key] HAVING COUNT(*)<>1)
                    OR EXISTS(SELECT 1 FROM @EntryFields WHERE CONVERT(varbinary(max),[key]) NOT IN(
                        CONVERT(varbinary(max),N'id'),CONVERT(varbinary(max),N'description'),CONVERT(varbinary(max),N'notes'),
                        CONVERT(varbinary(max),N'sourceLink'),CONVERT(varbinary(max),N'indicativePrice')) OR [type] NOT IN(0,1))
                    OR EXISTS(SELECT 1 FROM @EntryFields WHERE [key]=N'id' AND ([type]<>1 OR DATALENGTH([value])<>72 OR TRY_CONVERT(uniqueidentifier,[value]) IS NULL OR TRY_CONVERT(uniqueidentifier,[value])='00000000-0000-0000-0000-000000000000'))
                    OR EXISTS(SELECT TRY_CONVERT(uniqueidentifier,[value]) FROM @EntryFields WHERE [key]=N'id' GROUP BY TRY_CONVERT(uniqueidentifier,[value]) HAVING COUNT(*)>1)
                    OR EXISTS(SELECT 1 FROM @EntryFields WHERE ([key]=N'description' AND DATALENGTH([value])>1000) OR ([key]=N'notes' AND DATALENGTH([value])>4000)
                        OR ([key]=N'sourceLink' AND Purchasing.IsDraftSourceLink([value])<>1))
                    OR EXISTS(SELECT 1 FROM @EntryFields WHERE [key] IN(N'description',N'notes') AND [type]=1 AND DATALENGTH(TRIM(@Whitespace FROM [value]))=0)
                    OR EXISTS(SELECT 1 FROM @EntryFields WHERE [key]=N'description' AND [type]=1 AND CONVERT(varbinary(max),[value])<>CONVERT(varbinary(max),TRIM(@Whitespace FROM [value])))
                    THROW 50400,'Review the draft fields.',1;
                -- Canonical prices are strings with exactly four fractional digits, never rounded conversions.
                IF EXISTS(SELECT 1 FROM @EntryFields WHERE [key]=N'indicativePrice' AND [type]<>0 AND
                    (@Currency IS NULL OR DATALENGTH([value]) NOT BETWEEN 12 AND 40 OR [value] COLLATE Latin1_General_100_BIN2 LIKE N'%[^0-9.]%'
                    OR CHARINDEX(N'.',[value])<>DATALENGTH([value])/2-4 OR CHARINDEX(N'.',[value],CHARINDEX(N'.',[value])+1)>0
                    OR (LEFT([value],1)=N'0' AND CHARINDEX(N'.',[value])<>2) OR TRY_CONVERT(decimal(19,4),[value]) IS NULL))
                    THROW 50400,'Review the draft fields.',1;
                DECLARE @Content nvarchar(max)=N'{"sourceLinks":'+@Links+N',"entries":'+@Entries+N'}';
                IF DATALENGTH(@Content)>1048576 THROW 50400,'Review the draft fields.',1;
                DECLARE @SupplierId uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Draft,'$.supplierId')),
                    @ContactName nvarchar(max)=JSON_VALUE(@Draft,'$.supplierContactName'),@Email nvarchar(max)=JSON_VALUE(@Draft,'$.supplierEmail'),
                    @Phone nvarchar(max)=JSON_VALUE(@Draft,'$.supplierPhone'),@Website nvarchar(max)=JSON_VALUE(@Draft,'$.supplierWebsite'),
                    @PostalAddress nvarchar(max)=JSON_VALUE(@Draft,'$.supplierPostalAddress'),@SupplierOrderReference nvarchar(max)=JSON_VALUE(@Draft,'$.supplierOrderReference'),
                    @Platform nvarchar(max)=JSON_VALUE(@Draft,'$.platform');
                -- JSON_VALUE returns null for >4000 characters; validate the untruncated values first.
                IF EXISTS(SELECT 1 FROM @Fields WHERE [key] IN(N'supplierName',N'supplierContactName',N'supplierEmail',N'supplierPhone',N'supplierWebsite',N'supplierPostalAddress',N'supplierOrderReference',N'platform')
                    AND Purchasing.IsSupplierText([value],CASE [key] WHEN N'supplierEmail' THEN 254 WHEN N'supplierPhone' THEN 100 WHEN N'supplierWebsite' THEN 2048 WHEN N'supplierPostalAddress' THEN 2000 ELSE 200 END,CASE WHEN [key]=N'supplierPostalAddress' THEN 1 ELSE 0 END)=0)
                    OR EXISTS(SELECT 1 FROM @Fields WHERE [key]=N'supplierId' AND [type]<>0 AND (DATALENGTH([value])<>72 OR @SupplierId IS NULL OR @SupplierId='00000000-0000-0000-0000-000000000000'))
                    OR Purchasing.IsDraftSourceLink(@Website)=0
                    OR Purchasing.IsSupplierEmail(@Email)=0
                    THROW 50400,'Review supplier fields.',1;
                -- Lock order: request, existing draft, selected supplier, tenant counter. Supplier commands never lock drafts.
                DECLARE @ExistingSupplierId uniqueidentifier;
                IF @Operation='Update'
                    SELECT @ExistingSupplierId=SupplierId FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@TargetId AND IsDeleted=0;
                IF @SupplierId IS NOT NULL AND (@ExistingSupplierId IS NULL OR @ExistingSupplierId<>@SupplierId)
                BEGIN
                    DECLARE @SupplierArchived bit;
                    SELECT @SupplierArchived=IsArchived FROM Purchasing.Suppliers WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@SupplierId;
                    IF @SupplierArchived IS NULL THROW 50414,'Supplier not found.',1;
                    IF @SupplierArchived=1 THROW 50412,'Choose an active supplier.',1;
                END;
                DECLARE @CurrentVersion binary(8),@CurrentCurrency varchar(3),@Created datetimeoffset(7),@Now datetimeoffset(7)=SYSUTCDATETIME();
                IF @Operation='Update'
                BEGIN
                    SELECT @CurrentVersion=RowVersion,@CurrentCurrency=Currency,@Created=CreatedAtUtc
                        FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@TargetId AND IsDeleted=0;
                    IF @CurrentVersion IS NULL THROW 50404,'Draft not found.',1;
                    IF @CurrentVersion<>@ExpectedVersion THROW 50409,'The draft changed. Review the current version.',1;
                    IF @CurrentCurrency IS NOT NULL AND (@Currency IS NULL OR CONVERT(varbinary(max),CONVERT(nvarchar(3),@CurrentCurrency))<>CONVERT(varbinary(max),@Currency))
                        AND EXISTS(SELECT 1 FROM @EntryFields WHERE [key]=N'indicativePrice' AND [type]<>0)
                        THROW 50401,'Clear prices before changing currency.',1;
                    IF @Now<@Created SET @Now=@Created;
                    UPDATE Purchasing.DraftOrders SET Title=@Title,SupplierName=@Supplier,Currency=@Currency,Notes=@Notes,
                        SupplierId=@SupplierId,SupplierContactName=@ContactName,SupplierEmail=@Email,SupplierPhone=@Phone,SupplierWebsite=@Website,SupplierPostalAddress=@PostalAddress,SupplierOrderReference=@SupplierOrderReference,Platform=@Platform,
                        ContentSchemaVersion=1,ContentJson=@Content,UpdatedAtUtc=@Now,UpdatedByUserId=@ActorUserId
                        WHERE TenantId=@TenantId AND Id=@TargetId AND IsDeleted=0;
                END
                ELSE
                BEGIN
                    DECLARE @CounterResource nvarchar(255)=N'Purchasing.PoCounter:'+CONVERT(nvarchar(36),@TenantId),@PoNumber bigint;
                    EXEC @LockResult=sys.sp_getapplock @Resource=@CounterResource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
                    IF @LockResult<0 THROW 50411,'Purchase counter lock unavailable.',1;
                    SELECT @PoNumber=LastNumber FROM Purchasing.PurchaseOrderCounters WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId;
                    IF @PoNumber=9223372036854775807 THROW 50413,'Purchase numbering exhausted.',1;
                    IF @PoNumber IS NULL
                    BEGIN
                        SET @PoNumber=1;
                        INSERT Purchasing.PurchaseOrderCounters(TenantId,LastNumber) VALUES(@TenantId,@PoNumber);
                    END
                    ELSE
                    BEGIN
                        SET @PoNumber+=1;
                        UPDATE Purchasing.PurchaseOrderCounters SET LastNumber=@PoNumber WHERE TenantId=@TenantId;
                    END;
                    SET @TargetId=NEWID();
                    INSERT Purchasing.DraftOrders(PoNumber,SupplierId,SupplierContactName,SupplierEmail,SupplierPhone,SupplierWebsite,SupplierPostalAddress,SupplierOrderReference,Platform,Id,TenantId,Title,SupplierName,Currency,Notes,ContentSchemaVersion,ContentJson,CreatedAtUtc,UpdatedAtUtc,CreatedByUserId,UpdatedByUserId)
                        VALUES(@PoNumber,@SupplierId,@ContactName,@Email,@Phone,@Website,@PostalAddress,@SupplierOrderReference,@Platform,@TargetId,@TenantId,@Title,@Supplier,@Currency,@Notes,1,@Content,@Now,@Now,@ActorUserId,@ActorUserId);
                END;
                SELECT @CurrentVersion=RowVersion FROM Purchasing.DraftOrders WHERE TenantId=@TenantId AND Id=@TargetId AND IsDeleted=0;
                INSERT Purchasing.DraftOrderRequestReceipts(TenantId,RequestId,DraftOrderId,Operation,ActorUserId,ExpectedRowVersion,FingerprintVersion,InputFingerprint,ResultRowVersion,CompletedAtUtc)
                    VALUES(@TenantId,@RequestId,@TargetId,@Operation,@ActorUserId,@ExpectedVersion,2,@Fingerprint,@CurrentVersion,@Now);
                COMMIT;
                SELECT @RequestId RequestId,CONVERT(bit,0) Replayed,@TargetId DraftOrderId,@CurrentVersion SavedVersion,@Now CompletedAtUtc;
            END TRY
            BEGIN CATCH
                IF XACT_STATE()<>0 ROLLBACK;
                THROW;
            END CATCH;
        END;
        """;
}
