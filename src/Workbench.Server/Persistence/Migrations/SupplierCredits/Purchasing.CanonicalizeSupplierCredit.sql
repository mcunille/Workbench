-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.CanonicalizeSupplierCredit
  @Operation nvarchar(40),@Command nvarchar(max),@Canonical nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  IF @Operation<>N'PostSupplierCredit' OR @Command IS NULL OR DATALENGTH(@Command)>262144 OR ISJSON(@Command,OBJECT)<>1
    THROW 51000,'Invalid supplier credit envelope.',1;
  IF (SELECT COUNT(*) FROM OPENJSON(@Command))<>19 OR EXISTS(SELECT 1 FROM OPENJSON(@Command) WHERE
      [key] COLLATE Latin1_General_100_BIN2 NOT IN('schemaVersion','operation','expectedConfigurationVersion',
      'purchaseOrderId','expectedPurchaseOrderVersion','supplierId','currency','postingDate','creditId',
      'creditRevisionId','documentDate','effectiveDate','reference','reason','total','claims','duplicateResolutions','allocations','evidence')
      OR ([key]='schemaVersion' AND (type<>2 OR value<>'1'))
      OR ([key]='operation' AND value COLLATE Latin1_General_100_BIN2<>@Operation)
      OR ([key] IN('claims','duplicateResolutions','allocations') AND type<>4)
      OR ([key]='evidence' AND type<>5)
      OR ([key] NOT IN('schemaVersion','claims','duplicateResolutions','allocations','evidence') AND type<>1))
    THROW 51000,'Invalid supplier credit fields.',1;
  DECLARE @Nodes TABLE(Id int IDENTITY PRIMARY KEY,ParentId int,Depth int,Name nvarchar(4000) COLLATE Latin1_General_100_BIN2,
    JsonType int,Value nvarchar(max) COLLATE Latin1_General_100_BIN2,Canonical nvarchar(max) COLLATE Latin1_General_100_BIN2);
  INSERT @Nodes(ParentId,Depth,Name,JsonType,Value) VALUES(NULL,0,N'',5,@Command);
  DECLARE @Depth int=0;
  WHILE EXISTS(SELECT 1 FROM @Nodes WHERE Depth=@Depth AND JsonType IN(4,5))
  BEGIN
    IF @Depth>7 THROW 51000,'Supplier credit command is too deeply nested.',1;
    INSERT @Nodes(ParentId,Depth,Name,JsonType,Value)
      SELECT n.Id,@Depth+1,p.[key],p.type,p.value FROM @Nodes n CROSS APPLY OPENJSON(n.Value) p
      WHERE n.Depth=@Depth AND n.JsonType IN(4,5);
    IF EXISTS(SELECT 1 FROM @Nodes n JOIN @Nodes p ON p.Id=n.ParentId WHERE n.Depth=@Depth+1 AND p.JsonType=5
      GROUP BY n.ParentId,n.Name HAVING COUNT(*)>1)
      THROW 51000,'Duplicate supplier credit property.',1;
    SET @Depth+=1;
  END;
  IF EXISTS(SELECT 1 FROM @Nodes n JOIN @Nodes parent ON parent.Id=n.ParentId WHERE parent.JsonType=5
    AND DATALENGTH(n.Name)<>DATALENGTH(RTRIM(n.Name)))
    THROW 51000,'Supplier credit property names must be exact.',1;
  IF EXISTS(SELECT 1 FROM @Nodes WHERE Name IN('expectedConfigurationVersion','purchaseOrderId','supplierId',
      'creditId','creditRevisionId','claimId','billId','billRevisionId','recognitionEventId','documentId','revisionId','itemId')
      AND (JsonType<>1 OR DATALENGTH(Value)<>72 OR TRY_CONVERT(uniqueidentifier,Value) IS NULL
        OR TRY_CONVERT(uniqueidentifier,Value)='00000000-0000-0000-0000-000000000000'))
    OR EXISTS(SELECT 1 FROM @Nodes WHERE Name IN('expectedPurchaseOrderVersion','expectedItemVersion')
      AND (JsonType<>1 OR DATALENGTH(Value)<>36 OR TRY_CONVERT(binary(8),Value,1) IS NULL))
    OR EXISTS(SELECT 1 FROM @Nodes WHERE Name IN('postingDate','documentDate','effectiveDate')
      AND (JsonType<>1 OR DATALENGTH(Value)<>20 OR TRY_CONVERT(date,Value,23) IS NULL
        OR CONVERT(nvarchar(10),TRY_CONVERT(date,Value,23),23)<>Value))
    THROW 51000,'Invalid supplier credit identity, version or date.',1;
  IF EXISTS(SELECT 1 FROM @Nodes WHERE Name IN('amount','total') AND (JsonType<>1 OR LEN(Value) NOT BETWEEN 1 AND 29
      OR Value LIKE '%[^0-9.]%' OR LEFT(Value,1)='.' OR RIGHT(Value,1)='.'
      OR LEN(Value)-LEN(REPLACE(Value,'.',''))>1 OR LEN(LEFT(Value,CHARINDEX('.',Value+'.')-1))>24
      OR LEN(SUBSTRING(Value,CHARINDEX('.',Value+'.')+1,100))>4
      OR TRY_CONVERT(decimal(28,4),Value) IS NULL OR TRY_CONVERT(decimal(28,4),Value)<=0))
    OR EXISTS(SELECT 1 FROM @Nodes WHERE Name='currency' AND (DATALENGTH(Value)<>6 OR Value LIKE '%[^A-Z]%'))
    OR EXISTS(SELECT 1 FROM @Nodes WHERE Name IN('reference','reason') AND
      (DATALENGTH(Value) NOT BETWEEN 2 AND CASE Name WHEN 'reference' THEN 400 ELSE 4000 END OR LEN(TRIM(Value))=0))
    THROW 51000,'Invalid supplier credit amounts, currency or explanation.',1;
  IF (SELECT COUNT(*) FROM OPENJSON(@Command,'$.claims')) NOT BETWEEN 1 AND 1000
    OR (SELECT COUNT(*) FROM OPENJSON(@Command,'$.allocations'))>1000
    OR (SELECT COUNT(*) FROM OPENJSON(@Command,'$.duplicateResolutions'))>1000
    OR EXISTS(SELECT 1 FROM OPENJSON(@Command,'$.claims') c WHERE c.type<>5 OR (SELECT COUNT(*) FROM OPENJSON(c.value))<>6
      OR EXISTS(SELECT 1 FROM OPENJSON(c.value) p WHERE p.type<>1 OR p.[key] COLLATE Latin1_General_100_BIN2 NOT IN
        ('claimId','billId','billRevisionId','recognitionEventId','componentKey','amount')))
    OR EXISTS(SELECT 1 FROM OPENJSON(@Command,'$.claims') c WHERE DATALENGTH(JSON_VALUE(c.value,'$.componentKey')) NOT BETWEEN 2 AND 400)
    OR EXISTS(SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.claimId')) FROM OPENJSON(@Command,'$.claims') GROUP BY TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.claimId')) HAVING COUNT(*)>1)
    OR EXISTS(SELECT 1 FROM OPENJSON(@Command,'$.claims') GROUP BY TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.recognitionEventId')),JSON_VALUE(value,'$.componentKey') HAVING COUNT(*)>1)
    OR EXISTS(SELECT 1 FROM OPENJSON(@Command,'$.allocations') c WHERE c.type<>5 OR (SELECT COUNT(*) FROM OPENJSON(c.value))<>4
      OR EXISTS(SELECT 1 FROM OPENJSON(c.value) p WHERE p.type<>1 OR p.[key] COLLATE Latin1_General_100_BIN2 NOT IN('billId','itemId','expectedItemVersion','amount')))
    OR EXISTS(SELECT 1 FROM OPENJSON(@Command,'$.allocations') GROUP BY TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.itemId')) HAVING COUNT(*)>1)
    OR EXISTS(SELECT 1 FROM OPENJSON(@Command,'$.duplicateResolutions') c WHERE c.type<>5 OR (SELECT COUNT(*) FROM OPENJSON(c.value))<>3
      OR EXISTS(SELECT 1 FROM OPENJSON(c.value) p WHERE p.type<>1 OR p.[key] COLLATE Latin1_General_100_BIN2 NOT IN('creditId','creditRevisionId','reason')))
    THROW 51000,'Invalid supplier credit claims, resolutions or allocations.',1;
  DECLARE @Evidence nvarchar(max)=JSON_QUERY(@Command,'$.evidence');
  IF (SELECT COUNT(*) FROM OPENJSON(@Evidence))<>2 OR EXISTS(SELECT 1 FROM OPENJSON(@Evidence) WHERE
      [key] COLLATE Latin1_General_100_BIN2 NOT IN('documents','missingEvidenceReason')
      OR ([key]='documents' AND type<>4) OR ([key]='missingEvidenceReason' AND type NOT IN(0,1)))
    OR EXISTS(SELECT 1 FROM OPENJSON(@Evidence,'$.documents') c WHERE c.type<>5 OR (SELECT COUNT(*) FROM OPENJSON(c.value))<>2
      OR EXISTS(SELECT 1 FROM OPENJSON(c.value) p WHERE p.type<>1 OR p.[key] COLLATE Latin1_General_100_BIN2 NOT IN('documentId','revisionId')))
    OR ((SELECT COUNT(*) FROM OPENJSON(@Evidence,'$.documents'))=0
      AND COALESCE(LEN(TRIM(JSON_VALUE(@Evidence,'$.missingEvidenceReason'))),0)=0)
    OR DATALENGTH(JSON_VALUE(@Evidence,'$.missingEvidenceReason'))>4000
    THROW 51000,'Invalid supplier credit evidence.',1;
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
  IF DATALENGTH(@Canonical)>262144 THROW 51000,'Canonical supplier credit exceeds its bound.',1;
END;
