// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Persistence;

internal static class PurchaseRecognitionCommandValidation
{
    internal const string Sql = """
        CREATE PROCEDURE Purchasing.CanonicalizeRecognition
          @Command nvarchar(max),@IsCorrection bit,@Canonical nvarchar(max) OUTPUT
        AS
        BEGIN
          SET NOCOUNT ON;
          IF @Command IS NULL OR DATALENGTH(@Command)>262144 OR ISJSON(@Command,OBJECT)<>1
            THROW 51000,'Invalid recognition envelope.',1;
          -- Parse every property without narrowing first. The schema rejects duplicates, unknown fields,
          -- wrong JSON types and overlong values before JSON_VALUE or bounded SQL columns are used.
          DECLARE @Schema TABLE(Kind varchar(32),Name nvarchar(60) COLLATE Latin1_General_100_BIN2,JsonType int,Required bit,MaxBytes int);
          INSERT @Schema VALUES
            ('root','schemaVersion',2,1,2),('root','operation',1,1,8),('root','expectedConfigurationVersion',1,1,72),
            ('root','purchaseOrderId',1,1,72),('root','expectedPurchaseOrderVersion',1,1,36),('root','supplierId',1,1,72),
            ('root','currency',1,1,6),('root','postingDate',1,1,20),('root','units',4,1,262144),
            ('unit','unitId',1,1,72),('unit','classification',1,1,32),('unit','goodsReference',1,1,400),
            ('unit','quantity',1,1,58),('unit','quantityUnit',1,1,80),('unit','expectedPriorEventRevision',2,1,20),('unit','sides',4,1,262144),
            ('side','side',1,1,32),('side','eventRevision',2,1,20),('side','sourceId',1,1,72),('side','sourceRevision',1,1,72),
            ('side','sourceComponentKey',1,1,400),('side','subdivisionKey',1,1,400),('side','sourceQuantity',1,1,58),('side','sourceAmount',1,1,58),
            ('side','documentDate',1,1,20),('side','effectiveDate',1,1,20),('side','evidence',5,1,262144),('side','components',4,1,262144),
            ('component','componentKey',1,1,400),('component','kind',1,1,64),('component','amount',1,1,60),
            ('component','reason',1,0,4000),('component','assignedCostComponentKey',1,0,400),
            ('evidence','schemaVersion',2,1,2),('evidence','rationale',1,1,4000),
            ('evidence','sourceCapacityQuantity',1,1,58),('evidence','sourceCapacityAmount',1,1,58),
            ('evidence','recognitionBasis',1,0,80),('evidence','serviceDescription',1,0,4000),
            ('evidence','serviceStartDate',1,0,20),('evidence','serviceEndDate',1,0,20),('evidence','controlTransferDate',1,0,20),
            ('evidence','inTransit',3,0,10),('evidence','estimateBasis',1,0,4000),
            ('evidence','invoiceEligible',3,0,10),('evidence','presentObligation',3,0,10),('evidence','enforceableRight',3,0,10),
            ('evidence','taxPolicyReference',1,0,4000),('evidence','taxEntitlement',3,0,10),
            ('evidence','varianceAmount',1,0,60),('evidence','varianceReason',1,0,4000),
            ('evidence','varianceClassification',1,0,32),('evidence','inventoryAdjustmentState',1,0,80),
            ('evidence','documentRevision',1,0,400),('evidence','documentDigest',1,0,128);
          IF @IsCorrection=1
          BEGIN
            DELETE @Schema WHERE Kind='root';
            INSERT @Schema VALUES
              ('root','schemaVersion',2,1,2),('root','operation',1,1,14),('root','expectedConfigurationVersion',1,1,72),
              ('root','purchaseOrderId',1,1,72),('root','expectedPurchaseOrderVersion',1,1,36),('root','unitId',1,1,72),
              ('root','expectedEventRevisions',4,1,262144),('root','postingDate',1,1,20),('root','reason',1,1,4000),
              ('root','replacement',CASE WHEN JSON_VALUE(@Command,'$.operation')='Reverse' THEN 0 ELSE 5 END,1,262144),
              ('revision','side',1,1,32),('revision','eventRevision',2,1,20);
          END;
          DECLARE @Nodes TABLE(Id int IDENTITY PRIMARY KEY,ParentId int,Depth int,Kind varchar(32),Name nvarchar(4000) COLLATE Latin1_General_100_BIN2,
            JsonType int,Value nvarchar(max) COLLATE Latin1_General_100_BIN2,Canonical nvarchar(max) COLLATE Latin1_General_100_BIN2);
          INSERT @Nodes(ParentId,Depth,Kind,Name,JsonType,Value) VALUES(NULL,0,'root','',5,@Command);
          DECLARE @Depth int=0;
          WHILE EXISTS(SELECT 1 FROM @Nodes WHERE Depth=@Depth AND JsonType IN (4,5))
          BEGIN
            IF @Depth>7 THROW 51000,'Recognition input nesting is invalid.',1;
            INSERT @Nodes(ParentId,Depth,Kind,Name,JsonType,Value)
              SELECT n.Id,@Depth+1,CASE WHEN n.Kind='units' OR p.[key]='replacement' THEN 'unit' WHEN n.Kind='expectedEventRevisions' THEN 'revision' WHEN n.Kind='sides' THEN 'side' WHEN n.Kind='components' THEN 'component'
                WHEN p.[key] IN ('units','sides','components','evidence','expectedEventRevisions') THEN p.[key] ELSE '' END,p.[key],p.type,p.value
              FROM @Nodes n CROSS APPLY OPENJSON(n.Value) p WHERE n.Depth=@Depth AND n.JsonType IN (4,5);
            IF EXISTS(SELECT 1 FROM @Nodes n JOIN @Nodes p ON p.Id=n.ParentId
              LEFT JOIN @Schema s ON s.Kind=p.Kind AND s.Name=n.Name
              WHERE n.Depth=@Depth+1 AND ((p.JsonType=4 AND n.JsonType<>5) OR
                (p.JsonType=5 AND (s.Name IS NULL OR n.JsonType<>s.JsonType OR DATALENGTH(n.Value)>s.MaxBytes
                  OR DATALENGTH(n.Name)<>DATALENGTH(RTRIM(n.Name))))))
              OR EXISTS(SELECT 1 FROM @Nodes p JOIN @Schema s ON s.Kind=p.Kind AND s.Required=1
                WHERE p.Depth=@Depth AND p.JsonType=5 AND NOT EXISTS(SELECT 1 FROM @Nodes n WHERE n.ParentId=p.Id AND n.Name=s.Name))
              OR EXISTS(SELECT 1 FROM @Nodes n JOIN @Nodes p ON p.Id=n.ParentId WHERE n.Depth=@Depth+1 AND p.JsonType=5
                GROUP BY n.ParentId,n.Name HAVING COUNT(*)>1)
              THROW 51000,'Invalid recognition property shape.',1;
            SET @Depth+=1;
          END;
          IF EXISTS(SELECT 1 FROM @Nodes WHERE Name IN ('unitId','purchaseOrderId','supplierId','sourceId','sourceRevision','expectedConfigurationVersion')
              AND (DATALENGTH(Value)<>72 OR TRY_CONVERT(uniqueidentifier,Value) IS NULL OR TRY_CONVERT(uniqueidentifier,Value)='00000000-0000-0000-0000-000000000000'))
            OR EXISTS(SELECT 1 FROM @Nodes WHERE Name IN ('postingDate','documentDate','effectiveDate','serviceStartDate','serviceEndDate','controlTransferDate')
              AND (DATALENGTH(Value)<>20 OR TRY_CONVERT(date,Value,23) IS NULL OR CONVERT(nvarchar(10),TRY_CONVERT(date,Value,23),23)<>Value))
            OR EXISTS(SELECT 1 FROM @Nodes WHERE JsonType=1 AND LEN(LTRIM(RTRIM(Value)))=0)
            OR EXISTS(SELECT 1 FROM @Nodes WHERE Name IN ('classification','side','kind','recognitionBasis','varianceClassification','inventoryAdjustmentState') AND DATALENGTH(Value)<>DATALENGTH(RTRIM(Value)))
            OR EXISTS(SELECT 1 FROM @Nodes WHERE Name='schemaVersion' AND Value<>'1')
            OR (@IsCorrection=0 AND JSON_VALUE(@Command,'$.operation') COLLATE Latin1_General_100_BIN2<>'Post')
            OR (@IsCorrection=1 AND JSON_VALUE(@Command,'$.operation') COLLATE Latin1_General_100_BIN2 NOT IN ('Reverse','Replace'))
            THROW 51000,'Invalid recognition identities, dates or policy version.',1;
          -- Exact decimals: only rounding and an explicitly reviewed variance may be signed.
          IF EXISTS(SELECT 1 FROM @Nodes n
            CROSS APPLY (SELECT CASE WHEN (n.Name='varianceAmount' OR (n.Name='amount' AND EXISTS(SELECT 1 FROM @Nodes k WHERE k.ParentId=n.ParentId AND k.Name='kind' AND k.Value='Rounding')))
              AND LEFT(n.Value,1)='-' THEN SUBSTRING(n.Value,2,4000) ELSE n.Value END UnsignedValue) v
            WHERE n.Name IN ('quantity','sourceQuantity','sourceAmount','sourceCapacityQuantity','sourceCapacityAmount','amount','varianceAmount')
            AND (v.UnsignedValue COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9.]%' OR LEFT(v.UnsignedValue,1)='.' OR RIGHT(v.UnsignedValue,1)='.'
              OR LEN(v.UnsignedValue)=0 OR LEN(v.UnsignedValue)-LEN(REPLACE(v.UnsignedValue,'.',''))>1 OR TRY_CONVERT(decimal(38,6),n.Value) IS NULL
              OR LEN(SUBSTRING(v.UnsignedValue,CHARINDEX('.',v.UnsignedValue+'.')+1,100))>CASE WHEN n.Name IN ('quantity','sourceQuantity','sourceCapacityQuantity') THEN 6 ELSE 4 END
              OR LEN(LEFT(v.UnsignedValue,CHARINDEX('.',v.UnsignedValue+'.')-1))>CASE WHEN n.Name IN ('quantity','sourceQuantity','sourceCapacityQuantity') THEN 22 ELSE 24 END))
            THROW 51000,'Invalid exact decimal amount.',1;
          UPDATE @Nodes SET Canonical=CASE WHEN JsonType=1 THEN N'"'+STRING_ESCAPE(Value,'json')+N'"' WHEN JsonType=0 THEN N'null' ELSE Value END WHERE JsonType NOT IN (4,5);
          WHILE @Depth>=0
          BEGIN
            UPDATE n SET Canonical=CASE WHEN n.JsonType=4 THEN N'[' ELSE N'{' END+
              COALESCE((SELECT STRING_AGG(CONVERT(nvarchar(max),CASE WHEN parent.JsonType=5 THEN N'"'+STRING_ESCAPE(c.Name,'json')+N'":' ELSE N'' END)+c.Canonical,N',')
                WITHIN GROUP(ORDER BY CASE WHEN parent.JsonType=4 THEN TRY_CONVERT(int,c.Name) ELSE 0 END,c.Name)
                FROM @Nodes c JOIN @Nodes parent ON parent.Id=c.ParentId WHERE c.ParentId=n.Id),N'')+CASE WHEN n.JsonType=4 THEN N']' ELSE N'}' END
              FROM @Nodes n WHERE Depth=@Depth AND JsonType IN (4,5);
            SET @Depth-=1;
          END;
          SET @Canonical=(SELECT Canonical FROM @Nodes WHERE ParentId IS NULL);
          IF DATALENGTH(@Canonical)>262144 THROW 51000,'Canonical recognition input exceeds its bound.',1;
        END;
        """;
}
