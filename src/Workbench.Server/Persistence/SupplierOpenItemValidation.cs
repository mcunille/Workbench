// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Persistence;

internal static class SupplierOpenItemValidation
{
    // Shared structural normalization. Source, version, authority and capacity
    // decisions belong to the named command and the append participant.
    internal const string Sql = """
        CREATE PROCEDURE Purchasing.ValidateSupplierFinancialCommand
          @Operation nvarchar(40),@Command nvarchar(max),@Canonical nvarchar(max) OUTPUT
        AS
        BEGIN
          SET NOCOUNT ON;
          IF @Operation COLLATE Latin1_General_100_BIN2 NOT IN
            ('RecordSupplierPayment','ApplySupplierFunds','ReverseSupplierApplication','CorrectSupplierPayment')
            OR @Command IS NULL OR DATALENGTH(@Command)>262144 OR ISJSON(@Command,OBJECT)<>1
            THROW 51000,'Invalid supplier financial envelope.',1;
          DECLARE @Root TABLE(Name nvarchar(80) COLLATE Latin1_General_100_BIN2 PRIMARY KEY);
          INSERT @Root VALUES ('schemaVersion'),('operation'),('expectedConfigurationVersion'),
            ('purchaseOrderId'),('expectedPurchaseOrderVersion'),('supplierId'),('currency'),
            ('postingDate'),('paymentId'),('paymentRevisionId'),('paymentDate'),('effectiveDate'),
            ('amount'),('method'),('fundingAccountId'),('expectedFundingAccountVersion'),
            ('reference'),('notes'),('evidence'),('allocations'),('targets'),('fundingItemId'),
            ('expectedFundingItemVersion'),('applicationId'),('expectedApplicationVersion'),
            ('reason'),('reapplications'),('expectedPaymentVersion'),('expectedPlanFingerprint'),
            ('replacement');
          IF EXISTS(SELECT 1 FROM OPENJSON(@Command) p LEFT JOIN @Root r
            ON r.Name=p.[key] COLLATE Latin1_General_100_BIN2 WHERE r.Name IS NULL)
            OR EXISTS(SELECT [key] FROM OPENJSON(@Command)
              GROUP BY [key] HAVING COUNT(*)>1)
            OR JSON_VALUE(@Command,'$.schemaVersion') IS NULL
            OR JSON_VALUE(@Command,'$.schemaVersion')<>'1'
            OR JSON_VALUE(@Command,'$.operation') IS NULL
            OR JSON_VALUE(@Command,'$.operation') COLLATE Latin1_General_100_BIN2<>@Operation
            THROW 51000,'Invalid supplier financial properties.',1;
          DECLARE @Nodes TABLE(Id int IDENTITY PRIMARY KEY,ParentId int,Depth int,
            Name nvarchar(4000) COLLATE Latin1_General_100_BIN2,JsonType int,
            Value nvarchar(max) COLLATE Latin1_General_100_BIN2,
            Canonical nvarchar(max) COLLATE Latin1_General_100_BIN2);
          INSERT @Nodes(ParentId,Depth,Name,JsonType,Value) VALUES(NULL,0,N'',5,@Command);
          DECLARE @Depth int=0;
          WHILE EXISTS(SELECT 1 FROM @Nodes WHERE Depth=@Depth AND JsonType IN(4,5))
          BEGIN
            IF @Depth>7 THROW 51000,'Supplier financial command is too deeply nested.',1;
            INSERT @Nodes(ParentId,Depth,Name,JsonType,Value)
              SELECT n.Id,@Depth+1,p.[key],p.type,p.value FROM @Nodes n
                CROSS APPLY OPENJSON(n.Value) p
                WHERE n.Depth=@Depth AND n.JsonType IN(4,5);
            IF EXISTS(SELECT 1 FROM @Nodes n JOIN @Nodes p ON p.Id=n.ParentId
              WHERE n.Depth=@Depth+1 AND p.JsonType=5
              GROUP BY n.ParentId,n.Name HAVING COUNT(*)>1)
              THROW 51000,'Duplicate supplier financial property.',1;
            SET @Depth+=1;
          END;
          IF EXISTS(SELECT 1 FROM @Nodes n JOIN @Nodes p ON p.Id=n.ParentId
            JOIN @Nodes collection ON collection.Id=p.ParentId
            WHERE p.JsonType=5 AND collection.Name IN ('allocations','targets','reapplications')
              AND (n.Name COLLATE Latin1_General_100_BIN2 NOT IN
                ('billId','itemId','expectedItemVersion','amount','fundingItemId','expectedFundingItemVersion')
                OR n.JsonType<>1))
            THROW 51000,'Invalid supplier allocation target property.',1;
          IF EXISTS(SELECT 1 FROM @Nodes n JOIN @Nodes p ON p.Id=n.ParentId
            JOIN @Nodes collection ON collection.Id=p.ParentId
            WHERE p.JsonType=5 AND collection.Name IN ('allocations','targets','reapplications')
              AND n.Name='itemId' AND (DATALENGTH(n.Value)<>72
                OR TRY_CONVERT(uniqueidentifier,n.Value) IS NULL
                OR TRY_CONVERT(uniqueidentifier,n.Value)='00000000-0000-0000-0000-000000000000'))
            THROW 51000,'Invalid supplier allocation item identity.',1;
          IF EXISTS(SELECT 1 FROM @Nodes collection CROSS APPLY OPENJSON(collection.Value) target
            WHERE collection.Name IN ('allocations','targets','reapplications') AND collection.JsonType=4
            GROUP BY collection.Id,TRY_CONVERT(uniqueidentifier,JSON_VALUE(target.value,'$.itemId')) HAVING COUNT(*)>1)
            THROW 51000,'Duplicate supplier allocation target.',1;
          IF EXISTS(SELECT 1 FROM @Nodes WHERE Name IN('allocations','targets','reapplications')
            AND (JsonType<>4 OR (SELECT COUNT(*) FROM OPENJSON(Value))>1000))
            OR EXISTS(SELECT 1 FROM @Nodes WHERE Name='amount' AND
              (JsonType<>1 OR Value COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9.]%'
              OR LEFT(Value,1)='.' OR RIGHT(Value,1)='.' OR LEN(Value)=0
              OR LEN(Value)-LEN(REPLACE(Value,'.',''))>1
              OR LEN(LEFT(Value,CHARINDEX('.',Value+'.')-1))>24
              OR LEN(SUBSTRING(Value,CHARINDEX('.',Value+'.')+1,100))>4
              OR TRY_CONVERT(decimal(28,4),Value) IS NULL))
            THROW 51000,'Invalid supplier financial targets or amount.',1;
          UPDATE @Nodes SET Canonical=CASE WHEN JsonType=1 THEN N'"'+STRING_ESCAPE(Value,'json')+N'"'
            WHEN JsonType=0 THEN N'null' ELSE Value END WHERE JsonType NOT IN(4,5);
          WHILE @Depth>=0
          BEGIN
            UPDATE n SET Canonical=CASE WHEN n.JsonType=4 THEN N'[' ELSE N'{' END+
              COALESCE((SELECT STRING_AGG(CONVERT(nvarchar(max),CASE WHEN parent.JsonType=5
                THEN N'"'+STRING_ESCAPE(c.Name,'json')+N'":' ELSE N'' END)+c.Canonical,N',')
                WITHIN GROUP(ORDER BY CASE WHEN parent.JsonType=4 THEN TRY_CONVERT(int,c.Name) ELSE 0 END,c.Name)
                FROM @Nodes c JOIN @Nodes parent ON parent.Id=c.ParentId WHERE c.ParentId=n.Id),N'')+
              CASE WHEN n.JsonType=4 THEN N']' ELSE N'}' END FROM @Nodes n
              WHERE n.Depth=@Depth AND n.JsonType IN(4,5);
            SET @Depth-=1;
          END;
          SET @Canonical=(SELECT Canonical FROM @Nodes WHERE ParentId IS NULL);
          IF DATALENGTH(@Canonical)>262144 THROW 51000,'Canonical supplier command exceeds size limit.',1;
        END;
        """;
}
