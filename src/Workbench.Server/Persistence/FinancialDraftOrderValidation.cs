// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Persistence;

internal static partial class FinancialDraftOrderSchema
{
    private const string ValidateAdjustments = """
        DECLARE @OrderDiscount nvarchar(max)=JSON_QUERY(@Draft,'$.orderDiscount'),@Charges nvarchar(max)=JSON_QUERY(@Draft,'$.charges');
        DECLARE @DiscountObjects TABLE(EntryIndex int,Json nvarchar(max));
        INSERT @DiscountObjects SELECT EntryIndex,[value] FROM @EntryFields WHERE [key]=N'discount' AND [type]=5;
        IF @OrderDiscount IS NOT NULL INSERT @DiscountObjects VALUES(-1,@OrderDiscount);
        DECLARE @DiscountFields TABLE(EntryIndex int,[key] nvarchar(4000) COLLATE Latin1_General_100_BIN2,[value] nvarchar(max),[type] int);
        INSERT @DiscountFields SELECT EntryIndex,p.[key],p.[value],p.[type] FROM @DiscountObjects d CROSS APPLY OPENJSON(d.Json) p;
        IF EXISTS(SELECT 1 FROM @DiscountObjects d WHERE (SELECT COUNT(*) FROM @DiscountFields p WHERE p.EntryIndex=d.EntryIndex)<>2)
            OR EXISTS(SELECT EntryIndex,[key] FROM @DiscountFields GROUP BY EntryIndex,[key] HAVING COUNT(*)<>1)
            OR EXISTS(SELECT 1 FROM @DiscountFields WHERE [type]<>1 OR CONVERT(varbinary(max),[key]) NOT IN(CONVERT(varbinary(max),N'mode'),CONVERT(varbinary(max),N'value')))
            OR EXISTS(SELECT 1 FROM @DiscountFields WHERE [key]=N'mode' AND CONVERT(varbinary(max),[value]) NOT IN(CONVERT(varbinary(max),N'fixed'),CONVERT(varbinary(max),N'percentage')))
            THROW 50400,'Review the discount fields.',1;
        DECLARE @Discounts TABLE(EntryIndex int,Mode nvarchar(20),Value decimal(23,4));
        INSERT @Discounts SELECT EntryIndex,JSON_VALUE(Json,'$.mode'),TRY_CONVERT(decimal(23,4),JSON_VALUE(Json,'$.value')) FROM @DiscountObjects;
        IF EXISTS(SELECT 1 FROM @DiscountFields WHERE [key]=N'value' AND
            (DATALENGTH([value]) NOT BETWEEN 12 AND 48 OR [value] COLLATE Latin1_General_100_BIN2 LIKE N'%[^0-9.]%'
                OR CHARINDEX(N'.',[value])<>DATALENGTH([value])/2-4 OR CHARINDEX(N'.',[value],CHARINDEX(N'.',[value])+1)>0
                OR (LEFT([value],1)=N'0' AND CHARINDEX(N'.',[value])<>2) OR TRY_CONVERT(decimal(23,4),[value]) IS NULL))
            OR EXISTS(SELECT 1 FROM @Discounts WHERE (Mode=N'percentage' AND Value>100) OR (Mode=N'fixed' AND @Currency IS NULL))
            THROW 50400,'Review the discount value and currency.',1;
        IF (SELECT COUNT(*) FROM OPENJSON(@Charges))>50 OR EXISTS(SELECT 1 FROM OPENJSON(@Charges) WHERE [type]<>5)
            THROW 50400,'Review the charges.',1;
        DECLARE @ChargeFields TABLE(ChargeIndex int,[key] nvarchar(4000) COLLATE Latin1_General_100_BIN2,[value] nvarchar(max),[type] int);
        INSERT @ChargeFields SELECT CONVERT(int,c.[key]),p.[key],p.[value],p.[type] FROM OPENJSON(@Charges) c CROSS APPLY OPENJSON(c.[value]) p;
        IF EXISTS(SELECT 1 FROM OPENJSON(@Charges) c WHERE (SELECT COUNT(*) FROM @ChargeFields p WHERE p.ChargeIndex=CONVERT(int,c.[key]))<>9)
            OR EXISTS(SELECT ChargeIndex,[key] FROM @ChargeFields GROUP BY ChargeIndex,[key] HAVING COUNT(*)<>1)
            OR EXISTS(SELECT 1 FROM @ChargeFields WHERE CONVERT(varbinary(max),[key]) NOT IN(
                CONVERT(varbinary(max),N'id'),CONVERT(varbinary(max),N'category'),CONVERT(varbinary(max),N'label'),CONVERT(varbinary(max),N'amount'),
                CONVERT(varbinary(max),N'payeeKind'),CONVERT(varbinary(max),N'payeeName'),CONVERT(varbinary(max),N'amountStatus'),CONVERT(varbinary(max),N'reference'),CONVERT(varbinary(max),N'notes'))
                OR ([key] IN(N'id',N'category',N'label',N'payeeKind',N'amountStatus') AND [type]<>1) OR [type] NOT IN(0,1))
            OR EXISTS(SELECT 1 FROM @ChargeFields WHERE [key]=N'id' AND (DATALENGTH([value])<>72 OR TRY_CONVERT(uniqueidentifier,[value]) IS NULL OR TRY_CONVERT(uniqueidentifier,[value])='00000000-0000-0000-0000-000000000000'))
            OR EXISTS(SELECT TRY_CONVERT(uniqueidentifier,[value]) FROM @ChargeFields WHERE [key]=N'id' GROUP BY TRY_CONVERT(uniqueidentifier,[value]) HAVING COUNT(*)>1)
            OR EXISTS(SELECT 1 FROM @ChargeFields WHERE [key]=N'category' AND CONVERT(varbinary(max),[value]) NOT IN(
                CONVERT(varbinary(max),N'shipping'),CONVERT(varbinary(max),N'handling'),CONVERT(varbinary(max),N'insurance'),CONVERT(varbinary(max),N'salesTax'),CONVERT(varbinary(max),N'vatGst'),
                CONVERT(varbinary(max),N'customsDuty'),CONVERT(varbinary(max),N'otherTax'),CONVERT(varbinary(max),N'brokerage'),CONVERT(varbinary(max),N'paymentFee'),CONVERT(varbinary(max),N'inspection'),CONVERT(varbinary(max),N'other')))
            OR EXISTS(SELECT 1 FROM @ChargeFields WHERE [key]=N'payeeKind' AND CONVERT(varbinary(max),[value]) NOT IN(CONVERT(varbinary(max),N'supplier'),CONVERT(varbinary(max),N'thirdParty')))
            OR EXISTS(SELECT 1 FROM @ChargeFields WHERE [key]=N'amountStatus' AND CONVERT(varbinary(max),[value]) NOT IN(CONVERT(varbinary(max),N'estimated'),CONVERT(varbinary(max),N'confirmed')))
            OR EXISTS(SELECT 1 FROM @ChargeFields WHERE [key] IN(N'label',N'payeeName',N'reference',N'notes') AND [type]=1 AND
                (DATALENGTH([value])>CASE WHEN [key]=N'notes' THEN 4000 ELSE 400 END OR DATALENGTH(TRIM(@Whitespace FROM [value]))=0
                    OR CONVERT(varbinary(max),[value])<>CONVERT(varbinary(max),TRIM(@Whitespace FROM [value]))))
            OR EXISTS(SELECT 1 FROM @ChargeFields WHERE [key]=N'amount' AND [type]<>0 AND
                (@Currency IS NULL OR DATALENGTH([value]) NOT BETWEEN 12 AND 48 OR [value] COLLATE Latin1_General_100_BIN2 LIKE N'%[^0-9.]%'
                    OR CHARINDEX(N'.',[value])<>DATALENGTH([value])/2-4 OR CHARINDEX(N'.',[value],CHARINDEX(N'.',[value])+1)>0
                    OR (LEFT([value],1)=N'0' AND CHARINDEX(N'.',[value])<>2) OR TRY_CONVERT(decimal(23,4),[value]) IS NULL))
            THROW 50400,'Review the charge fields.',1;
        DECLARE @ChargeRows TABLE(Id uniqueidentifier,Category nvarchar(30),Label nvarchar(200),Amount decimal(23,4),AmountText nvarchar(24),PayeeKind nvarchar(20),PayeeName nvarchar(200),AmountStatus nvarchar(20),Notes nvarchar(2000));
        INSERT @ChargeRows SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE([value],'$.id')),JSON_VALUE([value],'$.category'),JSON_VALUE([value],'$.label'),
            TRY_CONVERT(decimal(23,4),JSON_VALUE([value],'$.amount')),JSON_VALUE([value],'$.amount'),JSON_VALUE([value],'$.payeeKind'),JSON_VALUE([value],'$.payeeName'),JSON_VALUE([value],'$.amountStatus'),JSON_VALUE([value],'$.notes') FROM OPENJSON(@Charges);
        IF EXISTS(SELECT 1 FROM @ChargeRows WHERE (PayeeKind=N'thirdParty' AND PayeeName IS NULL) OR (PayeeKind=N'supplier' AND PayeeName IS NOT NULL)
            OR (AmountStatus=N'confirmed' AND Amount IS NULL) OR (Category=N'other' AND Label=N'Other'))
            THROW 50400,'Review the charge payee, label and confirmation.',1;
        -- Arithmetic uses scaled integers. Modulo removes the fractional part before division;
        -- this avoids SQL decimal scale reduction rounding a midpoint before the intended round.
        DECLARE @FinancialLines TABLE(EntryIndex int,Gross decimal(25,0),Net decimal(25,0));
        INSERT @FinancialLines(EntryIndex,Gross)
            SELECT CONVERT(int,e.[key]),CASE WHEN JSON_VALUE(e.[value],'$.legacyPricing') IS NOT NULL OR JSON_QUERY(e.[value],'$.legacyPricing') IS NOT NULL
                OR JSON_VALUE(e.[value],'$.indicativePrice') IS NOT NULL THEN NULL
                WHEN JSON_VALUE(e.[value],'$.priceMode')=N'lineTotal' THEN CONVERT(decimal(23,0),TRY_CONVERT(decimal(23,4),JSON_VALUE(e.[value],'$.price'))*10000)
                WHEN JSON_VALUE(e.[value],'$.unitOfMeasure') IS NOT NULL THEN (Product-Product%10000)/10000+CASE WHEN Product%10000>=5000 THEN 1 ELSE 0 END END
            FROM OPENJSON(@Entries) e CROSS APPLY (SELECT
                CONVERT(decimal(13,0),TRY_CONVERT(decimal(13,4),JSON_VALUE(e.[value],'$.quantity'))*10000)
                *CONVERT(decimal(19,0),CASE WHEN JSON_VALUE(e.[value],'$.priceMode')=N'perUnit' THEN TRY_CONVERT(decimal(19,4),JSON_VALUE(e.[value],'$.price')) ELSE NULL END*10000) Product) scaled;
        IF EXISTS(SELECT 1 FROM @FinancialLines f JOIN @Discounts d ON d.EntryIndex=f.EntryIndex WHERE d.Mode=N'fixed' AND d.Value*10000>f.Gross)
            THROW 50400,'The line discount exceeds its eligible base.',1;
        UPDATE f SET Net=Gross-CASE WHEN d.EntryIndex IS NULL THEN 0 WHEN d.Mode=N'fixed' THEN d.Value*10000
            ELSE (Product-Product%1000000)/1000000+CASE WHEN Product%1000000>=500000 THEN 1 ELSE 0 END END
            FROM @FinancialLines f LEFT JOIN @Discounts d ON d.EntryIndex=f.EntryIndex
            CROSS APPLY (SELECT Gross*CONVERT(decimal(7,0),CASE WHEN d.Mode=N'percentage' THEN d.Value ELSE NULL END*10000) Product) scaled;
        DECLARE @Merchandise decimal(25,0),@Reduction decimal(25,0)=0;
        IF EXISTS(SELECT 1 FROM @FinancialLines) AND NOT EXISTS(SELECT 1 FROM @FinancialLines WHERE Net IS NULL)
            SELECT @Merchandise=SUM(Net) FROM @FinancialLines;
        IF EXISTS(SELECT 1 FROM @Discounts WHERE EntryIndex=-1 AND Mode=N'fixed' AND Value*10000>@Merchandise)
            THROW 50400,'The order discount exceeds its eligible base.',1;
        SELECT @Reduction=CASE WHEN Mode=N'fixed' THEN Value*10000 ELSE (Product-Product%1000000)/1000000+CASE WHEN Product%1000000>=500000 THEN 1 ELSE 0 END END
            FROM @Discounts CROSS APPLY (SELECT @Merchandise*CONVERT(decimal(7,0),CASE WHEN Mode=N'percentage' THEN Value ELSE NULL END*10000) Product) scaled WHERE EntryIndex=-1;
        IF COALESCE(@Merchandise-@Reduction,0)+COALESCE((SELECT SUM(CONVERT(decimal(25,0),Amount*10000)) FROM @ChargeRows),0)>=CONVERT(decimal(26,0),10000000000000000000000000)
            THROW 50400,'The purchase estimate exceeds the supported maximum.',1;
        """;
}
