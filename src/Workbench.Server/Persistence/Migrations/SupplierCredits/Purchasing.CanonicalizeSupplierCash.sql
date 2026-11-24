-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.CanonicalizeSupplierCash
  @Operation nvarchar(40),@Command nvarchar(max),@Canonical nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  IF @Operation IS NULL OR @Operation COLLATE Latin1_General_100_BIN2 NOT IN(N'RecordSupplierRefund',N'RecordSupplierRefundRepayment')
    OR @Command IS NULL OR DATALENGTH(@Command)>262144 OR ISJSON(@Command,OBJECT)<>1
    THROW 51000,'Invalid supplier cash envelope.',1;
  IF (SELECT COUNT(*) FROM OPENJSON(@Command) WHERE [key] NOT IN('reference','notes'))<>18
    OR EXISTS(SELECT 1 FROM OPENJSON(@Command) WHERE [key] COLLATE Latin1_General_100_BIN2 NOT IN
      ('schemaVersion','operation','expectedConfigurationVersion','purchaseOrderId','expectedPurchaseOrderVersion',
       'supplierId','currency','postingDate','cashSourceId','cashSourceRevisionId','actualDate','effectiveDate',
       'amount','method','fundingAccountId','expectedFundingAccountVersion','reference','notes','evidence','settlements')
      OR ([key]='schemaVersion' AND (type<>2 OR value<>'1'))
      OR ([key]='operation' AND value COLLATE Latin1_General_100_BIN2<>@Operation)
      OR ([key]='settlements' AND type<>4) OR ([key]='evidence' AND type<>5)
      OR ([key] IN('reference','notes') AND type NOT IN(0,1))
      OR ([key] NOT IN('schemaVersion','settlements','evidence','reference','notes') AND type<>1))
    THROW 51000,'Invalid supplier cash fields.',1;
  DECLARE @Nodes TABLE(Id int IDENTITY PRIMARY KEY,ParentId int,Depth int,Name nvarchar(4000) COLLATE Latin1_General_100_BIN2,
    JsonType int,Value nvarchar(max) COLLATE Latin1_General_100_BIN2,Canonical nvarchar(max) COLLATE Latin1_General_100_BIN2);
  INSERT @Nodes(ParentId,Depth,Name,JsonType,Value) VALUES(NULL,0,N'',5,@Command);
  DECLARE @Depth int=0;
  WHILE EXISTS(SELECT 1 FROM @Nodes WHERE Depth=@Depth AND JsonType IN(4,5))
  BEGIN
    IF @Depth>7 THROW 51000,'Supplier cash command is too deeply nested.',1;
    INSERT @Nodes(ParentId,Depth,Name,JsonType,Value)
      SELECT n.Id,@Depth+1,p.[key],p.type,p.value FROM @Nodes n CROSS APPLY OPENJSON(n.Value) p
      WHERE n.Depth=@Depth AND n.JsonType IN(4,5);
    IF EXISTS(SELECT 1 FROM @Nodes n JOIN @Nodes parent ON parent.Id=n.ParentId
      WHERE n.Depth=@Depth+1 AND parent.JsonType=5 GROUP BY n.ParentId,n.Name HAVING COUNT(*)>1)
      THROW 51000,'Duplicate supplier cash property.',1;
    SET @Depth+=1;
  END;
  IF EXISTS(SELECT 1 FROM @Nodes n JOIN @Nodes parent ON parent.Id=n.ParentId WHERE parent.JsonType=5
    AND DATALENGTH(n.Name)<>DATALENGTH(RTRIM(n.Name)))
    THROW 51000,'Supplier cash property names must be exact.',1;
  IF EXISTS(SELECT 1 FROM @Nodes WHERE Name IN('expectedConfigurationVersion','purchaseOrderId','supplierId',
      'cashSourceId','cashSourceRevisionId','fundingAccountId','expectedFundingAccountVersion','itemId','documentId','revisionId')
      AND (JsonType<>1 OR DATALENGTH(Value)<>72 OR TRY_CONVERT(uniqueidentifier,Value) IS NULL
        OR TRY_CONVERT(uniqueidentifier,Value)='00000000-0000-0000-0000-000000000000'))
    OR EXISTS(SELECT 1 FROM @Nodes WHERE Name IN('expectedPurchaseOrderVersion','expectedItemVersion')
      AND (JsonType<>1 OR DATALENGTH(Value)<>36 OR TRY_CONVERT(binary(8),Value,1) IS NULL))
    OR EXISTS(SELECT 1 FROM @Nodes WHERE Name IN('actualDate','effectiveDate','postingDate')
      AND (JsonType<>1 OR DATALENGTH(Value)<>20 OR TRY_CONVERT(date,Value,23) IS NULL
        OR CONVERT(nvarchar(10),TRY_CONVERT(date,Value,23),23)<>Value))
    THROW 51000,'Invalid supplier cash identity, version or date.',1;
  IF EXISTS(SELECT 1 FROM @Nodes WHERE Name='amount' AND (JsonType<>1 OR LEN(Value) NOT BETWEEN 1 AND 29
      OR DATALENGTH(Value)<>DATALENGTH(RTRIM(Value))
      OR Value LIKE '%[^0-9.]%' OR LEFT(Value,1)='.' OR RIGHT(Value,1)='.'
      OR LEN(Value)-LEN(REPLACE(Value,'.',''))>1 OR LEN(LEFT(Value,CHARINDEX('.',Value+'.')-1))>24
      OR LEN(SUBSTRING(Value,CHARINDEX('.',Value+'.')+1,100))>4
      OR TRY_CONVERT(decimal(28,4),Value) IS NULL OR TRY_CONVERT(decimal(28,4),Value)<=0))
    OR EXISTS(SELECT 1 FROM @Nodes WHERE Name='currency' AND (DATALENGTH(Value)<>6 OR Value LIKE '%[^A-Z]%'))
    OR EXISTS(SELECT 1 FROM @Nodes WHERE Name='method' AND Value NOT IN('Bank','Cash'))
    OR EXISTS(SELECT 1 FROM @Nodes WHERE Name IN('reference','notes','missingEvidenceReason') AND JsonType<>0
      AND (DATALENGTH(Value)>CASE WHEN Name='reference' THEN 400 ELSE 4000 END
        OR (Name='missingEvidenceReason' AND LEN(TRIM(Value))=0)))
    THROW 51000,'Invalid supplier cash amount, method or explanation.',1;
  IF (SELECT COUNT(*) FROM OPENJSON(@Command,'$.settlements')) NOT BETWEEN 1 AND 1000
    OR EXISTS(SELECT 1 FROM OPENJSON(@Command,'$.settlements') target WHERE target.type<>5
      OR (SELECT COUNT(*) FROM OPENJSON(target.value))<>3
      OR EXISTS(SELECT 1 FROM OPENJSON(target.value) p WHERE p.type<>1
        OR p.[key] COLLATE Latin1_General_100_BIN2 NOT IN('itemId','expectedItemVersion','amount')))
    OR EXISTS(SELECT 1 FROM OPENJSON(@Command,'$.settlements') GROUP BY TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.itemId')) HAVING COUNT(*)>1)
    THROW 51000,'Invalid supplier cash settlements.',1;
  DECLARE @Evidence nvarchar(max)=JSON_QUERY(@Command,'$.evidence');
  IF (SELECT COUNT(*) FROM OPENJSON(@Evidence))<>2 OR EXISTS(SELECT 1 FROM OPENJSON(@Evidence) WHERE
      [key] COLLATE Latin1_General_100_BIN2 NOT IN('documents','missingEvidenceReason')
      OR ([key]='documents' AND type<>4) OR ([key]='missingEvidenceReason' AND type NOT IN(0,1)))
    OR (SELECT COUNT(*) FROM OPENJSON(@Evidence,'$.documents'))>1000
    OR EXISTS(SELECT 1 FROM OPENJSON(@Evidence,'$.documents') target WHERE target.type<>5
      OR (SELECT COUNT(*) FROM OPENJSON(target.value))<>2 OR EXISTS(SELECT 1 FROM OPENJSON(target.value) p
        WHERE p.type<>1 OR p.[key] COLLATE Latin1_General_100_BIN2 NOT IN('documentId','revisionId')))
    OR EXISTS(SELECT 1 FROM OPENJSON(@Evidence,'$.documents') GROUP BY TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.documentId')) HAVING COUNT(*)>1)
    OR ((SELECT COUNT(*) FROM OPENJSON(@Evidence,'$.documents'))=0
      AND COALESCE(LEN(TRIM(JSON_VALUE(@Evidence,'$.missingEvidenceReason'))),0)=0)
    THROW 51000,'Invalid supplier cash evidence.',1;
  UPDATE @Nodes SET Canonical=CASE WHEN JsonType=1 THEN N'"'+STRING_ESCAPE(Value,'json')+N'"'
    WHEN JsonType=0 THEN N'null' ELSE Value END WHERE JsonType NOT IN(4,5);
  WHILE @Depth>=0
  BEGIN
    UPDATE n SET Canonical=CASE WHEN n.JsonType=4 THEN N'[' ELSE N'{' END+
      COALESCE((SELECT STRING_AGG(CONVERT(nvarchar(max),CASE WHEN parent.JsonType=5 THEN N'"'+STRING_ESCAPE(c.Name,'json')+N'":' ELSE N'' END)+c.Canonical,N',')
        WITHIN GROUP(ORDER BY CASE WHEN parent.JsonType=4 THEN TRY_CONVERT(int,c.Name) ELSE 0 END,c.Name)
        FROM @Nodes c JOIN @Nodes parent ON parent.Id=c.ParentId WHERE c.ParentId=n.Id),N'')+
      CASE WHEN n.JsonType=4 THEN N']' ELSE N'}' END FROM @Nodes n WHERE n.Depth=@Depth AND n.JsonType IN(4,5);
    SET @Depth-=1;
  END;
  SET @Canonical=(SELECT Canonical FROM @Nodes WHERE ParentId IS NULL);
  IF DATALENGTH(@Canonical)>262144 THROW 51000,'Canonical supplier cash exceeds its bound.',1;
END;
