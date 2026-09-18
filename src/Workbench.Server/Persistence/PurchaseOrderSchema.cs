// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;
namespace Workbench.Server.Persistence;

internal static class PurchaseOrderSchema
{
    internal const string MigrationId = "20260918060000_AddPurchaseOrderCommitment";
    internal static void Create(MigrationBuilder migration)
    {
        migration.Sql("""
            ALTER TABLE Purchasing.DraftOrders ADD CONSTRAINT CK_DraftOrders_State CHECK
              ((State='Draft' AND OrderDate IS NULL AND Revision=0) OR (State='Ordered' AND OrderDate IS NOT NULL AND Revision>0 AND IsDeleted=0));
            CREATE TABLE Purchasing.PurchaseOrderRevisions(
              TenantId uniqueidentifier NOT NULL, DraftOrderId uniqueidentifier NOT NULL, Revision int NOT NULL,
              OrderDate date NOT NULL, ActorUserId uniqueidentifier NOT NULL, RecordedAtUtc datetimeoffset(7) NOT NULL,
              Reason nvarchar(2000) NULL, CalculationPolicyVersion int NOT NULL, DraftJson nvarchar(max) NOT NULL,
              CalculationJson nvarchar(max) NOT NULL, PoReference varchar(23) NOT NULL,
              CONSTRAINT PK_PurchaseOrderRevisions PRIMARY KEY(TenantId,DraftOrderId,Revision),
              CONSTRAINT FK_PurchaseOrderRevisions_Order FOREIGN KEY(TenantId,DraftOrderId) REFERENCES Purchasing.DraftOrders(TenantId,Id),
              CONSTRAINT FK_PurchaseOrderRevisions_Actor FOREIGN KEY(TenantId,ActorUserId) REFERENCES [Identity].Users(TenantId,Id),
              CONSTRAINT CK_PurchaseOrderRevisions_Content CHECK(Revision>0 AND ISJSON(DraftJson,OBJECT)=1 AND ISJSON(CalculationJson,OBJECT)=1 AND CalculationPolicyVersion=1
                AND DATEPART(TZOFFSET,RecordedAtUtc)=0 AND ((Revision=1 AND Reason IS NULL) OR (Revision>1 AND DATALENGTH(Reason)>0))));
            CREATE TABLE Purchasing.PurchaseOrderReceipts(
              TenantId uniqueidentifier NOT NULL, RequestId uniqueidentifier NOT NULL, DraftOrderId uniqueidentifier NOT NULL,
              ActorUserId uniqueidentifier NOT NULL, Operation varchar(6) NOT NULL, InputFingerprint binary(32) NOT NULL,
              ExpectedRowVersion binary(8) NOT NULL, ResultRowVersion binary(8) NOT NULL, CompletedAtUtc datetimeoffset(7) NOT NULL, Revision int NOT NULL,
              CONSTRAINT PK_PurchaseOrderReceipts PRIMARY KEY(TenantId,RequestId),
              CONSTRAINT FK_PurchaseOrderReceipts_Revision FOREIGN KEY(TenantId,DraftOrderId,Revision) REFERENCES Purchasing.PurchaseOrderRevisions(TenantId,DraftOrderId,Revision));
            """);
        migration.Sql("""
            CREATE SECURITY POLICY Purchasing.PurchaseOrderHistoryPolicy
              ADD FILTER PREDICATE Security.fn_tenant_access(TenantId) ON Purchasing.PurchaseOrderRevisions,
              ADD BLOCK PREDICATE Security.fn_tenant_access(TenantId) ON Purchasing.PurchaseOrderRevisions AFTER INSERT,
              ADD FILTER PREDICATE Security.fn_tenant_access(TenantId) ON Purchasing.PurchaseOrderReceipts,
              ADD BLOCK PREDICATE Security.fn_tenant_access(TenantId) ON Purchasing.PurchaseOrderReceipts AFTER INSERT WITH(STATE=ON);
            GRANT SELECT ON Purchasing.PurchaseOrderRevisions TO workbench_web;
            GRANT SELECT ON Purchasing.PurchaseOrderReceipts TO workbench_web;
            DENY INSERT,UPDATE,DELETE ON Purchasing.PurchaseOrderRevisions TO workbench_web;
            DENY INSERT,UPDATE,DELETE ON Purchasing.PurchaseOrderReceipts TO workbench_web;
            """);
        migration.Sql("""
            CREATE FUNCTION Purchasing.FormatPurchaseAmount(@Scaled decimal(25,0)) RETURNS varchar(27) AS
            BEGIN
              RETURN CONVERT(varchar(21),CONVERT(decimal(21,0),(@Scaled-@Scaled%10000)/10000))+'.'+RIGHT('0000'+CONVERT(varchar(4),@Scaled%10000),4);
            END;
            """);
        // Extract the complete installed financial validator, including supplier correction checks.
        // Anchors fail closed rather than silently installing a reduced validation boundary.
        migration.Sql("""
            DECLARE @Source nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.SaveDraftOrder'));
            DECLARE @Start int=CHARINDEX(N'DECLARE @Fields TABLE',@Source),@End int=CHARINDEX(N'DECLARE @CurrentVersion binary(8)',@Source);
            DECLARE @Transition int=CHARINDEX(N'DECLARE @SavedContent nvarchar(max)',@Source),@TransitionEnd int=CHARINDEX(N'IF @Now<@Created',@Source);
            IF @Start=0 OR @End<=@Start OR @Transition=0 OR @TransitionEnd<=@Transition
                OR CHARINDEX(N'DECLARE @FinancialLines TABLE',@Source)=0 THROW 50020,'Unsupported purchase validation predecessor.',1;
            DECLARE @Validator nvarchar(max)=N'CREATE PROCEDURE Purchasing.ValidatePurchaseOrderContent @Draft nvarchar(max),@TenantId uniqueidentifier,@TargetId uniqueidentifier,@Calculation nvarchar(max) OUTPUT AS BEGIN SET NOCOUNT ON;
                DECLARE @Operation varchar(6)=''Update'',@CurrentCurrency varchar(3);
                SELECT @CurrentCurrency=Currency FROM Purchasing.DraftOrders WHERE TenantId=@TenantId AND Id=@TargetId;'
                +SUBSTRING(@Source,@Start,@End-@Start)+SUBSTRING(@Source,@Transition,@TransitionEnd-@Transition)+N'
                IF @Supplier IS NULL OR @Currency IS NULL OR NOT EXISTS(SELECT 1 FROM OPENJSON(@Entries))
                    OR EXISTS(SELECT 1 FROM OPENJSON(@Entries) WHERE JSON_VALUE([value],''$.description'') IS NULL OR JSON_VALUE([value],''$.quantity'') IS NULL
                    OR JSON_VALUE([value],''$.unitOfMeasure'') IS NULL OR JSON_VALUE([value],''$.indicativePrice'') IS NOT NULL OR JSON_QUERY([value],''$.legacyPricing'') IS NOT NULL)
                    THROW 50400,''Complete the supplier, currency and every ordered line; resolve retained quotes.'',1;
                DECLARE @SupplierTotal decimal(25,0),@ThirdPartyTotal decimal(25,0),@LineDiscountTotal decimal(25,0),@GrossTotal decimal(25,0);
                SELECT @GrossTotal=SUM(Gross) FROM @FinancialLines;
                IF @Merchandise IS NOT NULL SELECT @LineDiscountTotal=SUM(Gross-Net) FROM @FinancialLines;
                IF @Merchandise IS NULL SET @Reduction=NULL;
                IF NOT EXISTS(SELECT 1 FROM @ChargeRows WHERE PayeeKind=N''supplier'' AND Amount IS NULL)
                    SELECT @SupplierTotal=COALESCE(SUM(CONVERT(decimal(25,0),Amount*10000)),0) FROM @ChargeRows WHERE PayeeKind=N''supplier'';
                IF NOT EXISTS(SELECT 1 FROM @ChargeRows WHERE PayeeKind=N''thirdParty'' AND Amount IS NULL)
                    SELECT @ThirdPartyTotal=COALESCE(SUM(CONVERT(decimal(25,0),Amount*10000)),0) FROM @ChargeRows WHERE PayeeKind=N''thirdParty'';
                SET @Calculation=(SELECT JSON_QUERY((SELECT LOWER(JSON_VALUE(e.[value],''$.id'')) id,
                    Purchasing.FormatPurchaseAmount(f.Gross) gross,Purchasing.FormatPurchaseAmount(f.Gross) discountBase,
                    Purchasing.FormatPurchaseAmount(f.Gross-f.Net) discountAmount,Purchasing.FormatPurchaseAmount(f.Net) net
                    FROM @FinancialLines f JOIN OPENJSON(@Entries) e ON CONVERT(int,e.[key])=f.EntryIndex ORDER BY f.EntryIndex FOR JSON PATH,INCLUDE_NULL_VALUES)) lines,
                    (SELECT COUNT(*) FROM @FinancialLines WHERE Gross IS NULL) incompleteLineCount,
                    Purchasing.FormatPurchaseAmount(@GrossTotal) merchandiseEstimate,Purchasing.FormatPurchaseAmount(@LineDiscountTotal) lineDiscountTotal,
                    Purchasing.FormatPurchaseAmount(@Merchandise) merchandiseNet,Purchasing.FormatPurchaseAmount(@Merchandise) orderDiscountBase,
                    Purchasing.FormatPurchaseAmount(@Reduction) orderDiscountAmount,Purchasing.FormatPurchaseAmount(@Merchandise-@Reduction) discountedMerchandise,
                    Purchasing.FormatPurchaseAmount(@SupplierTotal) supplierCharges,Purchasing.FormatPurchaseAmount(@ThirdPartyTotal) thirdPartyCharges,
                    Purchasing.FormatPurchaseAmount(@Merchandise-@Reduction+@SupplierTotal) supplierEstimate,
                    Purchasing.FormatPurchaseAmount(@Merchandise-@Reduction+@SupplierTotal+@ThirdPartyTotal) purchaseEstimate,
                    (SELECT COUNT(*) FROM @ChargeRows WHERE Amount IS NULL) incompleteChargeCount FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES);
                END;';
            EXEC sys.sp_executesql @Validator;
            """);
        migration.Sql("""
            CREATE PROCEDURE Purchasing.ProjectRetainedPurchaseOrderEntries
              @Entries nvarchar(max),@Projected nvarchar(max) OUTPUT
            AS
            BEGIN
              SET NOCOUNT ON;
              SET @Projected=N'[]';
              DECLARE @Index int=0,@Count int=(SELECT COUNT(*) FROM OPENJSON(@Entries)),@Entry nvarchar(max),@Modern nvarchar(max);
              WHILE @Index<@Count
              BEGIN
                SET @Entry=JSON_QUERY(@Entries,N'$['+CONVERT(nvarchar(10),@Index)+N']');
                DECLARE @Quantity nvarchar(28)=JSON_VALUE(@Entry,'$.quantity'),@Unit nvarchar(20)=JSON_VALUE(@Entry,'$.unitOfMeasure'),
                  @UnitPrice nvarchar(40)=JSON_VALUE(@Entry,'$.unitPrice'),@PricingUnit nvarchar(20)=JSON_VALUE(@Entry,'$.pricingUnit'),
                  @Denominator nvarchar(28)=JSON_VALUE(@Entry,'$.pricePerQuantity'),@PricingQuantity nvarchar(28)=JSON_VALUE(@Entry,'$.pricingQuantity'),
                  @Price nvarchar(27)=NULL,@Mode nvarchar(20)=N'perUnit',@Legacy nvarchar(max)=NULL;
                DECLARE @EffectiveQuantity nvarchar(28)=CASE WHEN @Unit=@PricingUnit THEN @Quantity ELSE @PricingQuantity END;
                DECLARE @Q decimal(13,0)=TRY_CONVERT(decimal(13,4),@EffectiveQuantity)*10000,
                  @P decimal(19,0)=TRY_CONVERT(decimal(19,4),@UnitPrice)*10000,@D decimal(13,0)=TRY_CONVERT(decimal(13,4),@Denominator)*10000;
                IF @Quantity IS NOT NULL AND @Unit IS NOT NULL AND @UnitPrice IS NOT NULL AND @PricingUnit IS NOT NULL AND @Denominator IS NOT NULL AND @EffectiveQuantity IS NOT NULL AND @D>0
                BEGIN
                  DECLARE @Product decimal(33,0)=@Q*@P;
                  DECLARE @Gross decimal(25,0)=(@Product-@Product%@D)/@D+CASE WHEN (@Product%@D)*2>=@D THEN 1 ELSE 0 END;
                  DECLARE @UnitNumerator decimal(23,0)=@P*10000;
                  DECLARE @UnitScaled decimal(23,0)=(@UnitNumerator-@UnitNumerator%@D)/@D;
                  SET @Quantity=@EffectiveQuantity; SET @Unit=@PricingUnit;
                  DECLARE @CandidateProduct decimal(33,0)=CASE WHEN @UnitScaled<10000000000000000000 THEN @Q*CONVERT(decimal(19,0),@UnitScaled) END;
                  DECLARE @CandidateGross decimal(25,0)=(@CandidateProduct-@CandidateProduct%10000)/10000+CASE WHEN @CandidateProduct%10000>=5000 THEN 1 ELSE 0 END;
                  IF @UnitNumerator%@D=0 AND @UnitScaled<10000000000000000000 AND @CandidateGross=@Gross
                    SET @Price=Purchasing.FormatPurchaseAmount(@UnitScaled);
                  ELSE
                  BEGIN
                    SET @Mode=N'lineTotal'; SET @Price=Purchasing.FormatPurchaseAmount(@Gross);
                  END;
                END
                ELSE IF @UnitPrice IS NOT NULL OR @PricingUnit IS NOT NULL OR @Denominator IS NOT NULL OR @PricingQuantity IS NOT NULL
                  SET @Legacy=(SELECT @Quantity quantity,@Unit unitOfMeasure,@UnitPrice unitPrice,@PricingUnit pricingUnit,@Denominator pricePerQuantity,@PricingQuantity pricingQuantity FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES);
                SET @Modern=(SELECT LOWER(JSON_VALUE(@Entry,'$.id')) id,JSON_VALUE(@Entry,'$.description') description,JSON_VALUE(@Entry,'$.notes') notes,
                  JSON_VALUE(@Entry,'$.sourceLink') sourceLink,JSON_VALUE(@Entry,'$.indicativePrice') indicativePrice,@Quantity quantity,@Unit unitOfMeasure,
                  @Mode priceMode,@Price price,JSON_QUERY(@Legacy) legacyPricing,JSON_VALUE(@Entry,'$.supplierSku') supplierSku,JSON_VALUE(@Entry,'$.itemType') itemType,
                  CAST(NULL AS nvarchar(max)) discount FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES);
                SET @Projected=JSON_MODIFY(@Projected,'append $',JSON_QUERY(@Modern));
                SET @Index+=1;
              END;
            END;
            """);
        migration.Sql(PurchaseOrderContentComparison.Create);
        migration.Sql(Command);
        migration.Sql("GRANT EXECUTE ON Purchasing.SavePurchaseOrder TO workbench_web;");
        foreach (var (name, anchor, target) in new[] {
            ("SaveDraftOrder", "IF @CurrentVersion<>@ExpectedVersion THROW 50409,'The draft changed. Review the current version.',1;", "@TargetId"),
            ("DeleteDraftOrder", "IF @CurrentVersion<>@ExpectedRowVersion THROW 50409,'The draft changed. Review the current version.',1;", "@DraftOrderId") })
        {
            var replacement = anchor + $"\nIF EXISTS(SELECT 1 FROM Purchasing.DraftOrders WHERE TenantId=@TenantId AND Id={target} AND State<>'Draft') THROW 50415,'Ordered purchases require an amendment.',1;";
            migration.Sql($"""
                DECLARE @Command nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.{name}'));
                IF @Command IS NULL OR CHARINDEX(N'{anchor.Replace("'", "''", StringComparison.Ordinal)}',@Command)=0 THROW 50020,'Unsupported draft state predecessor.',1;
                SET @Command=REPLACE(REPLACE(@Command,N'CREATE PROCEDURE',N'ALTER PROCEDURE'),N'{anchor.Replace("'", "''", StringComparison.Ordinal)}',N'{replacement.Replace("'", "''", StringComparison.Ordinal)}');
                EXEC sys.sp_executesql @Command;
                """);
        }
        migration.Sql($"""
            DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Security.ReadDatabaseReadiness'));
            SET @Readiness=REPLACE(REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE'),N'20260918020000_IntegrateBetaDraftFinancialAdjustments',N'{MigrationId}');
            EXEC sys.sp_executesql @Readiness;
            """);
    }

    private const string Command = """
        CREATE PROCEDURE Purchasing.SavePurchaseOrder
          @RequestId uniqueidentifier,@ActorUserId uniqueidentifier,@TargetId uniqueidentifier,@ExpectedVersion varbinary(max),
          @Operation varchar(6),@OrderDate nvarchar(max),@Reason nvarchar(max),@Draft nvarchar(max),@Calculation nvarchar(max)
        AS
        BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'));
          IF @TenantId IS NULL OR NOT EXISTS(SELECT 1 FROM Security.fn_tenant_access(@TenantId))
            OR NOT EXISTS(SELECT 1 FROM [Identity].Users WHERE TenantId=@TenantId AND Id=@ActorUserId AND State=1)
            OR NOT EXISTS(SELECT 1 FROM Tenancy.Tenants WHERE Id=@TenantId AND IsEnabled=1) THROW 50403,'Current tenant authority required.',1;
          IF @RequestId IS NULL OR @RequestId='00000000-0000-0000-0000-000000000000' OR @TargetId IS NULL OR @ExpectedVersion IS NULL OR DATALENGTH(@ExpectedVersion)<>8
            OR @Operation IS NULL OR CONVERT(varbinary(max),@Operation) NOT IN(CONVERT(varbinary(max),'Commit'),CONVERT(varbinary(max),'Amend'))
            OR @OrderDate IS NULL OR DATALENGTH(@OrderDate)<>20 OR TRY_CONVERT(date,@OrderDate,23) IS NULL
            OR CONVERT(nvarchar(10),TRY_CONVERT(date,@OrderDate,23),23) COLLATE Latin1_General_100_BIN2<>@OrderDate
            OR (@Operation='Commit' AND (@Reason IS NOT NULL OR @Draft IS NOT NULL))
            OR (@Operation='Amend' AND (@Reason IS NULL OR DATALENGTH(@Reason) NOT BETWEEN 2 AND 4000 OR DATALENGTH(TRIM(@Reason))=0 OR ISJSON(@Draft,OBJECT)<>1 OR @Draft IS NULL OR DATALENGTH(@Draft)>8388608))
            OR @Calculation IS NULL OR ISJSON(@Calculation,OBJECT)<>1 OR DATALENGTH(@Calculation)>1048576 THROW 50400,'Review purchase input.',1;
          DECLARE @Whitespace nvarchar(64)=NCHAR(9)+NCHAR(10)+NCHAR(11)+NCHAR(12)+NCHAR(13)+NCHAR(32)+NCHAR(133)+NCHAR(160)+NCHAR(5760)+NCHAR(8192)+NCHAR(8193)+NCHAR(8194)+NCHAR(8195)+NCHAR(8196)+NCHAR(8197)+NCHAR(8198)+NCHAR(8199)+NCHAR(8200)+NCHAR(8201)+NCHAR(8202)+NCHAR(8232)+NCHAR(8233)+NCHAR(8239)+NCHAR(8287)+NCHAR(12288);
          IF @Operation='Amend' AND (DATALENGTH(TRIM(@Whitespace FROM @Reason))=0 OR CONVERT(varbinary(max),@Reason)<>CONVERT(varbinary(max),TRIM(@Whitespace FROM @Reason))) THROW 50400,'Enter a trimmed amendment reason.',1;
          DECLARE @Canonical nvarchar(max)=(SELECT @Operation operation,@TargetId targetId,CONVERT(varchar(max),@ExpectedVersion,2) expectedVersion,@OrderDate orderDate,@Reason reason,JSON_QUERY(@Draft) draft FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES);
          DECLARE @Fingerprint binary(32)=HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Canonical));
          BEGIN TRY
            BEGIN TRANSACTION;
            DECLARE @Lock int,@Resource nvarchar(255)=N'Purchasing.PurchaseRequest:'+CONVERT(nvarchar(36),@TenantId)+N':'+CONVERT(nvarchar(36),@RequestId);
            EXEC @Lock=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000;
            IF @Lock<0 THROW 50409,'Purchase request lock unavailable.',1;
            IF NOT EXISTS(SELECT 1 FROM Purchasing.DraftOrders WHERE TenantId=@TenantId AND Id=@TargetId AND IsDeleted=0) THROW 50404,'Purchase not found.',1;
            IF EXISTS(SELECT 1 FROM Purchasing.PurchaseOrderReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId)
            BEGIN
              IF NOT EXISTS(SELECT 1 FROM Purchasing.PurchaseOrderReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId AND ActorUserId=@ActorUserId AND DraftOrderId=@TargetId
                AND Operation=@Operation AND ExpectedRowVersion=@ExpectedVersion AND InputFingerprint=@Fingerprint) THROW 50410,'Request identifier was used for different input.',1;
              COMMIT;
              SELECT RequestId,CONVERT(bit,1) Replayed,DraftOrderId,ResultRowVersion SavedVersion,CompletedAtUtc,Revision FROM Purchasing.PurchaseOrderReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId;
              RETURN;
            END;
            DECLARE @Version binary(8),@State varchar(7),@Revision int,@Currency varchar(3),@PreviousDate date,@Created datetimeoffset(7),@Now datetimeoffset(7)=SYSUTCDATETIME();
            SELECT @Version=RowVersion,@State=State,@Revision=Revision,@Currency=Currency,@PreviousDate=OrderDate,@Created=CreatedAtUtc
              FROM Purchasing.DraftOrders WITH(UPDLOCK,HOLDLOCK) WHERE TenantId=@TenantId AND Id=@TargetId AND IsDeleted=0;
            IF @Version IS NULL THROW 50404,'Purchase not found.',1;
            IF @Version<>@ExpectedVersion THROW 50409,'Purchase changed; review the current version.',1;
            IF (@Operation='Commit' AND @State<>'Draft') OR (@Operation='Amend' AND @State<>'Ordered') THROW 50415,'Purchase state does not permit this operation.',1;
            DECLARE @SavedEntries nvarchar(max),@StoredSchema smallint;
            SELECT @SavedEntries=JSON_QUERY(ContentJson,'$.entries'),@StoredSchema=ContentSchemaVersion FROM Purchasing.DraftOrders WHERE TenantId=@TenantId AND Id=@TargetId;
            IF @StoredSchema IN(1,2) EXEC Purchasing.ProjectRetainedPurchaseOrderEntries @SavedEntries,@SavedEntries OUTPUT;
            IF @StoredSchema=3
            BEGIN
                DECLARE @EntryIndex int=0,@EntryCount int=(SELECT COUNT(*) FROM OPENJSON(@SavedEntries)),@Path nvarchar(100),@Entry nvarchar(max);
                WHILE @EntryIndex<@EntryCount
                BEGIN
                    SET @Path=N'$['+CONVERT(nvarchar(10),@EntryIndex)+N']';
                    SET @Entry=JSON_QUERY(@SavedEntries,@Path);
                    IF NOT EXISTS(SELECT 1 FROM OPENJSON(@Entry) WHERE [key]=N'discount')
                        SET @Entry=LEFT(@Entry,LEN(@Entry)-1)+N',' + N'"discount":null}';
                    SET @SavedEntries=JSON_MODIFY(@SavedEntries,@Path,JSON_QUERY(@Entry));
                    SET @EntryIndex+=1;
                END;
            END;
            DECLARE @SavedDraft nvarchar(max)=(SELECT Title title,SupplierName supplierName,Currency currency,Notes notes,JSON_QUERY(ContentJson,'$.sourceLinks') sourceLinks,JSON_QUERY(@SavedEntries) entries,
              LOWER(CONVERT(varchar(36),SupplierId)) supplierId,SupplierContactName supplierContactName,SupplierEmail supplierEmail,SupplierPhone supplierPhone,SupplierWebsite supplierWebsite,SupplierPostalAddress supplierPostalAddress,
              SupplierOrderReference supplierOrderReference,Platform platform,JSON_QUERY(ContentJson,'$.orderDiscount') orderDiscount,JSON_QUERY(COALESCE(JSON_QUERY(ContentJson,'$.charges'),N'[]')) charges
              FROM Purchasing.DraftOrders WHERE TenantId=@TenantId AND Id=@TargetId FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES);
            IF @Operation='Commit' SET @Draft=@SavedDraft;
            IF @Operation='Amend' AND (@Currency IS NULL OR JSON_VALUE(@Draft,'$.currency') IS NULL OR CONVERT(varbinary(max),CONVERT(nvarchar(3),@Currency))<>CONVERT(varbinary(max),JSON_VALUE(@Draft,'$.currency')))
              THROW 50416,'Currency is fixed after commitment.',1;
            EXEC Purchasing.ValidatePurchaseOrderContent @Draft,@TenantId,@TargetId,@Calculation OUTPUT;
            IF @Operation='Amend' AND @PreviousDate=CONVERT(date,@OrderDate,23)
            BEGIN
              DECLARE @SameContent bit;
              EXEC Purchasing.ComparePurchaseOrderContent @Draft,@SavedDraft,@SameContent OUTPUT;
              IF @SameContent=1 THROW 50417,'An amendment must change the saved purchase.',1;
            END;
            SET @Revision+=1;
            IF @Now<@Created SET @Now=@Created;
            UPDATE Purchasing.DraftOrders SET State='Ordered',OrderDate=CONVERT(date,@OrderDate,23),Revision=@Revision,
              Title=j.title,SupplierName=j.supplierName,Currency=j.currency,Notes=j.notes,SupplierId=j.supplierId,SupplierContactName=j.supplierContactName,SupplierEmail=j.supplierEmail,SupplierPhone=j.supplierPhone,
              SupplierWebsite=j.supplierWebsite,SupplierPostalAddress=j.supplierPostalAddress,SupplierOrderReference=j.supplierOrderReference,Platform=j.platform,
              ContentSchemaVersion=4,ContentJson=(SELECT JSON_QUERY(@Draft,'$.sourceLinks') sourceLinks,JSON_QUERY(@Draft,'$.entries') entries,JSON_QUERY(@Draft,'$.orderDiscount') orderDiscount,JSON_QUERY(@Draft,'$.charges') charges FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES),
              UpdatedAtUtc=@Now,UpdatedByUserId=@ActorUserId
              FROM Purchasing.DraftOrders d CROSS APPLY OPENJSON(@Draft) WITH(title nvarchar(200),supplierName nvarchar(200),currency varchar(3),notes nvarchar(max),supplierId uniqueidentifier,supplierContactName nvarchar(200),supplierEmail nvarchar(254),supplierPhone nvarchar(100),supplierWebsite nvarchar(2048),supplierPostalAddress nvarchar(2000),supplierOrderReference nvarchar(200),platform nvarchar(200)) j
              WHERE d.TenantId=@TenantId AND d.Id=@TargetId;
            INSERT Purchasing.PurchaseOrderRevisions(TenantId,DraftOrderId,Revision,OrderDate,ActorUserId,RecordedAtUtc,Reason,CalculationPolicyVersion,DraftJson,CalculationJson,PoReference)
              SELECT @TenantId,@TargetId,@Revision,CONVERT(date,@OrderDate,23),@ActorUserId,@Now,@Reason,1,@Draft,@Calculation,'PO-'+CASE WHEN PoNumber<1000000 THEN RIGHT('000000'+CONVERT(varchar(20),PoNumber),6) ELSE CONVERT(varchar(20),PoNumber) END FROM Purchasing.DraftOrders WHERE TenantId=@TenantId AND Id=@TargetId;
            SELECT @Version=RowVersion FROM Purchasing.DraftOrders WHERE TenantId=@TenantId AND Id=@TargetId;
            INSERT Purchasing.PurchaseOrderReceipts VALUES(@TenantId,@RequestId,@TargetId,@ActorUserId,@Operation,@Fingerprint,@ExpectedVersion,@Version,@Now,@Revision);
            COMMIT;
            SELECT @RequestId RequestId,CONVERT(bit,0) Replayed,@TargetId DraftOrderId,@Version SavedVersion,@Now CompletedAtUtc,@Revision Revision;
          END TRY BEGIN CATCH IF XACT_STATE()<>0 ROLLBACK; THROW; END CATCH;
        END;
        """;
}
