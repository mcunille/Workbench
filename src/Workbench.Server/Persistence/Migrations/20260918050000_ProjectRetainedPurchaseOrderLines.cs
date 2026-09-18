// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;
namespace Workbench.Server.Persistence.Migrations;

public partial class ProjectRetainedPurchaseOrderLines : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Keep applied preview migrations immutable. This storage-only projection matches
        // DraftOrderInput.Upgrade while the purchase command holds the saved order lock.
        migrationBuilder.Sql("""
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
        migrationBuilder.Sql("""
            DECLARE @Command nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.SavePurchaseOrder'));
            IF @Command IS NULL OR CHARINDEX(N'IF @StoredSchema=3',@Command)=0 THROW 50020,'Unsupported retained purchase projection predecessor.',1;
            SET @Command=REPLACE(@Command,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Command=REPLACE(@Command,N'IF @StoredSchema=3',N'IF @StoredSchema IN(1,2) EXEC Purchasing.ProjectRetainedPurchaseOrderEntries @SavedEntries,@SavedEntries OUTPUT;
                IF @StoredSchema=3');
            EXEC sys.sp_executesql @Command;
            DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Security.ReadDatabaseReadiness'));
            SET @Readiness=REPLACE(REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE'),N'20260918040000_HardenPurchaseOrderCommitmentValidation',N'20260918050000_ProjectRetainedPurchaseOrderLines');
            EXEC sys.sp_executesql @Readiness;
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("THROW 50020, 'Retained purchase projection requires forward correction or guarded recovery.', 1;");
}
