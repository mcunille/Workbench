// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Persistence;

internal static class SupplierBillValidation
{
    internal const string NormalizeSql = """
        CREATE FUNCTION Purchasing.NormalizeBillReference(@Value nvarchar(200)) RETURNS nvarchar(200)
        AS BEGIN
          DECLARE @Result nvarchar(200)=N'',@Index int=1,@Code int,@Space bit=0;
          WHILE @Index<=DATALENGTH(@Value)/2
          BEGIN
            SET @Code=UNICODE(SUBSTRING(@Value COLLATE Latin1_General_100_BIN2,@Index,1));
            IF @Code IN (9,10,13,32) SET @Space=CASE WHEN DATALENGTH(@Result)>0 THEN 1 ELSE 0 END;
            ELSE BEGIN
              IF @Space=1 SET @Result+=N' ';
              SET @Result+=NCHAR(CASE WHEN @Code BETWEEN 97 AND 122 THEN @Code-32 ELSE @Code END);
              SET @Space=0;
            END;
            SET @Index+=1;
          END;
          RETURN NULLIF(@Result,N'');
        END;
        """;

    internal const string Sql = """
        CREATE PROCEDURE Purchasing.CanonicalizeSupplierBill @Command nvarchar(max),@Canonical nvarchar(max) OUTPUT
        AS BEGIN
          SET NOCOUNT ON;
          IF @Command IS NULL OR ISJSON(@Command,OBJECT)<>1 OR DATALENGTH(@Command)>262144
            THROW 51000,'Invalid supplier bill envelope.',1;
          DECLARE @Schema TABLE(Kind varchar(20),Name nvarchar(80) COLLATE Latin1_General_100_BIN2,JsonType int,Required bit,MaxBytes int);
          INSERT @Schema VALUES
            ('root','schemaVersion',2,1,2),('root','operation',1,1,32),('root','billId',1,1,72),
            ('root','billVersion',1,0,36),('root','purchaseOrderId',1,0,72),('root','supplierId',1,0,72),('root','currency',1,0,6),
            ('root','expectedPurchaseOrderVersion',1,1,36),('root','revision',5,0,262144),('root','reason',1,0,4000),
            ('root','revisionId',1,0,72),('root','rationale',1,0,4000),('root','resolutions',4,0,262144),
            ('root','expectedConfigurationVersion',1,0,72),
            ('revision','kind',1,1,16),('revision','reference',1,0,400),('revision','documentDate',1,0,20),
            ('revision','effectiveDate',1,0,20),('revision','postingDate',1,0,20),('revision','dueDate',1,0,20),
            ('revision','terms',1,0,4000),('revision','total',1,0,60),('revision','units',4,0,262144),
            ('revision','documents',4,0,262144),('revision','missingEvidenceReason',1,0,4000),('revision','predecessorBillId',1,0,72),
            ('resolution','conflictingBillId',1,1,72),('resolution','conflictingRevisionId',1,1,72),('resolution','reason',1,1,4000),
            ('document','documentId',1,1,72),('document','revisionId',1,1,72),
            ('unit','componentKey',1,1,400),('unit','unitId',1,1,72),('unit','classification',1,1,32),
            ('unit','goodsReference',1,1,400),('unit','quantity',1,1,58),('unit','quantityUnit',1,1,80),
            ('unit','expectedPriorEventRevision',2,1,20),('unit','components',4,1,262144),('unit','evidence',5,1,262144),
            ('component','componentKey',1,1,400),('component','kind',1,1,64),('component','amount',1,1,60),
            ('component','reason',1,0,4000),('component','assignedCostComponentKey',1,0,400),
            ('evidence','rationale',1,1,4000),('evidence','invoiceEligible',3,1,10),('evidence','presentObligation',3,1,10),
            ('evidence','enforceableRight',3,1,10),('evidence','taxPolicyReference',1,0,4000),('evidence','taxEntitlement',3,0,10),
            ('evidence','varianceAmount',1,0,60),('evidence','varianceReason',1,0,4000),('evidence','varianceClassification',1,0,32);
          DECLARE @Nodes TABLE(Id int IDENTITY PRIMARY KEY,ParentId int,Depth int,Kind varchar(20),Name nvarchar(4000) COLLATE Latin1_General_100_BIN2,
            JsonType int,Value nvarchar(max) COLLATE Latin1_General_100_BIN2,Canonical nvarchar(max) COLLATE Latin1_General_100_BIN2);
          INSERT @Nodes(ParentId,Depth,Kind,Name,JsonType,Value) VALUES(NULL,0,'root','',5,@Command);
          DECLARE @Depth int=0;
          WHILE EXISTS(SELECT 1 FROM @Nodes WHERE Depth=@Depth AND JsonType IN(4,5))
          BEGIN
            IF @Depth>6 THROW 51000,'Invalid supplier bill nesting.',1;
            INSERT @Nodes(ParentId,Depth,Kind,Name,JsonType,Value)
              SELECT n.Id,@Depth+1,CASE n.Kind WHEN 'units' THEN 'unit' WHEN 'components' THEN 'component'
                WHEN 'documents' THEN 'document' WHEN 'resolutions' THEN 'resolution' ELSE CASE WHEN p.[key] IN('revision','units','components','evidence','documents','resolutions') THEN p.[key] ELSE '' END END,p.[key],p.type,p.value
                FROM @Nodes n CROSS APPLY OPENJSON(n.Value) p WHERE n.Depth=@Depth AND n.JsonType IN(4,5);
            IF EXISTS(SELECT 1 FROM @Nodes n JOIN @Nodes p ON p.Id=n.ParentId LEFT JOIN @Schema s ON s.Kind=p.Kind AND s.Name=n.Name
                WHERE n.Depth=@Depth+1 AND ((p.JsonType=4 AND n.JsonType<>5) OR (p.JsonType=5 AND
                  (s.Name IS NULL OR n.JsonType<>s.JsonType OR DATALENGTH(n.Value)>s.MaxBytes OR DATALENGTH(n.Name)<>DATALENGTH(RTRIM(n.Name))))))
              OR EXISTS(SELECT 1 FROM @Nodes p JOIN @Schema s ON s.Kind=p.Kind AND s.Required=1 WHERE p.Depth=@Depth AND p.JsonType=5
                AND NOT EXISTS(SELECT 1 FROM @Nodes n WHERE n.ParentId=p.Id AND n.Name=s.Name))
              OR EXISTS(SELECT 1 FROM @Nodes n JOIN @Nodes p ON p.Id=n.ParentId WHERE n.Depth=@Depth+1 AND p.JsonType=5
                GROUP BY n.ParentId,n.Name HAVING COUNT(*)>1)
              THROW 51000,'Invalid supplier bill property shape.',1;
            SET @Depth+=1;
          END;
          IF EXISTS(SELECT 1 FROM @Nodes WHERE Name IN('billId','purchaseOrderId','supplierId','revisionId','unitId','documentId','predecessorBillId','conflictingBillId','conflictingRevisionId','expectedConfigurationVersion')
              AND (DATALENGTH(Value)<>72 OR TRY_CONVERT(uniqueidentifier,Value) IS NULL OR TRY_CONVERT(uniqueidentifier,Value)='00000000-0000-0000-0000-000000000000'))
            OR EXISTS(SELECT 1 FROM @Nodes WHERE Name IN('billVersion','expectedPurchaseOrderVersion') AND
              (DATALENGTH(Value)<>36 OR LEFT(Value,2)<>'0x' OR TRY_CONVERT(binary(8),Value,1) IS NULL))
            OR EXISTS(SELECT 1 FROM @Nodes WHERE Name IN('documentDate','effectiveDate','postingDate','dueDate') AND
              (DATALENGTH(Value)<>20 OR TRY_CONVERT(date,Value,23) IS NULL OR CONVERT(nvarchar(10),TRY_CONVERT(date,Value,23),23)<>Value))
            OR EXISTS(SELECT 1 FROM @Nodes WHERE JsonType=1 AND LEN(TRIM(NCHAR(9)+NCHAR(10)+NCHAR(13)+NCHAR(32) FROM Value))=0)
            OR EXISTS(SELECT 1 FROM @Nodes WHERE Name='schemaVersion' AND Value<>'1')
            OR EXISTS(SELECT 1 FROM @Nodes WHERE Name='currency' AND (DATALENGTH(Value)<>6 OR Value LIKE '%[^A-Z]%'))
            OR EXISTS(SELECT 1 FROM @Nodes WHERE Name='operation' AND Value NOT IN('Create','Revise','Abandon','Review','Post'))
            OR EXISTS(SELECT 1 FROM @Nodes n JOIN @Nodes p ON n.ParentId=p.Id WHERE p.Kind='revision' AND n.Name='kind' AND n.Value NOT IN('Invoice','ProForma'))
            THROW 51000,'Invalid supplier bill value.',1;
          IF EXISTS(SELECT 1 FROM @Nodes n CROSS APPLY(SELECT CASE WHEN LEFT(n.Value,1)='-' AND
              (n.Name='varianceAmount' OR EXISTS(SELECT 1 FROM @Nodes k WHERE k.ParentId=n.ParentId AND k.Name='kind' AND k.Value='Rounding'))
                THEN SUBSTRING(n.Value,2,4000) ELSE n.Value END V) d
              WHERE n.Name IN('amount','total','quantity','varianceAmount') AND (d.V LIKE '%[^0-9.]%' OR LEFT(d.V,1)='.' OR RIGHT(d.V,1)='.'
                OR LEN(d.V)=0 OR LEN(d.V)-LEN(REPLACE(d.V,'.',''))>1 OR TRY_CONVERT(decimal(38,6),n.Value) IS NULL
                OR LEN(SUBSTRING(d.V,CHARINDEX('.',d.V+'.')+1,100))>CASE WHEN n.Name='quantity' THEN 6 ELSE 4 END
                OR LEN(LEFT(d.V,CHARINDEX('.',d.V+'.')-1))>CASE WHEN n.Name='quantity' THEN 22 ELSE 24 END))
            THROW 51000,'Invalid exact bill decimal.',1;
          UPDATE @Nodes SET Canonical=CASE WHEN JsonType=1 THEN N'"'+STRING_ESCAPE(Value,'json')+N'"' ELSE Value END WHERE JsonType NOT IN(4,5);
          WHILE @Depth>=0
          BEGIN
            UPDATE n SET Canonical=CASE WHEN n.JsonType=4 THEN N'[' ELSE N'{' END+
              COALESCE((SELECT STRING_AGG(CONVERT(nvarchar(max),CASE WHEN parent.JsonType=5 THEN N'"'+STRING_ESCAPE(c.Name,'json')+N'":' ELSE N'' END)+c.Canonical,N',')
                WITHIN GROUP(ORDER BY CASE WHEN parent.JsonType=4 THEN TRY_CONVERT(int,c.Name) ELSE 0 END,c.Name)
                FROM @Nodes c JOIN @Nodes parent ON parent.Id=c.ParentId WHERE c.ParentId=n.Id),N'')+CASE WHEN n.JsonType=4 THEN N']' ELSE N'}' END
              FROM @Nodes n WHERE Depth=@Depth AND JsonType IN(4,5);
            SET @Depth-=1;
          END;
          SET @Canonical=(SELECT Canonical FROM @Nodes WHERE ParentId IS NULL);
          IF DATALENGTH(@Canonical)>262144 THROW 51000,'Canonical bill exceeds size limit.',1;
        END;
        """;
}
