-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.CanonicalizeSupplierSourceCorrection
  @Operation nvarchar(40),@Command nvarchar(max),@Canonical nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  IF @Operation IS NULL OR @Operation COLLATE Latin1_General_100_BIN2 NOT IN
      ('CorrectSupplierCredit','CorrectSupplierBill','CorrectSupplierRefund','CorrectSupplierRefundRepayment')
    OR @Command IS NULL OR ISJSON(@Command,OBJECT)<>1 OR DATALENGTH(@Command)>262144
    THROW 51000,'Invalid supplier source correction envelope.',1;
  IF (SELECT COUNT(*) FROM OPENJSON(@Command) WHERE [key]<>'expectedPlanFingerprint')<>14
    OR EXISTS(SELECT 1 FROM OPENJSON(@Command) WHERE [key] COLLATE Latin1_General_100_BIN2 NOT IN
      ('schemaVersion','operation','expectedConfigurationVersion','purchaseOrderId','expectedPurchaseOrderVersion','supplierId','currency','postingDate',
       'sourceId','expectedSourceVersion','reason','replacement','sourceDecisions','reapplications','expectedPlanFingerprint')
      OR ([key]='schemaVersion' AND (type<>2 OR value<>'1'))
      OR ([key]='operation' AND value COLLATE Latin1_General_100_BIN2<>@Operation)
      OR ([key] IN('sourceDecisions','reapplications') AND type<>4)
      OR ([key]='replacement' AND type NOT IN(0,5))
      OR ([key] NOT IN('schemaVersion','sourceDecisions','reapplications','replacement') AND type<>1))
    THROW 51000,'Complete explicit supplier correction decisions required.',1;
  DECLARE @Nodes TABLE(Id int IDENTITY PRIMARY KEY,ParentId int,Depth int,Name nvarchar(4000) COLLATE Latin1_General_100_BIN2,
    JsonType int,Value nvarchar(max) COLLATE Latin1_General_100_BIN2,Canonical nvarchar(max) COLLATE Latin1_General_100_BIN2);
  INSERT @Nodes(ParentId,Depth,Name,JsonType,Value) VALUES(NULL,0,N'',5,@Command);
  DECLARE @Depth int=0;
  WHILE EXISTS(SELECT 1 FROM @Nodes WHERE Depth=@Depth AND JsonType IN(4,5))
  BEGIN
    IF @Depth>7 THROW 51000,'Supplier source correction command is too deeply nested.',1;
    INSERT @Nodes(ParentId,Depth,Name,JsonType,Value)
      SELECT n.Id,@Depth+1,p.[key],p.type,p.value FROM @Nodes n CROSS APPLY OPENJSON(n.Value) p
      WHERE n.Depth=@Depth AND n.JsonType IN(4,5);
    IF EXISTS(SELECT 1 FROM @Nodes n JOIN @Nodes p ON p.Id=n.ParentId WHERE n.Depth=@Depth+1 AND p.JsonType=5
      GROUP BY n.ParentId,n.Name HAVING COUNT(*)>1)
      THROW 51000,'Duplicate supplier source correction property.',1;
    SET @Depth+=1;
  END;
  IF EXISTS(SELECT 1 FROM @Nodes n JOIN @Nodes parent ON parent.Id=n.ParentId WHERE parent.JsonType=5
    AND DATALENGTH(n.Name)<>DATALENGTH(RTRIM(n.Name)))
    THROW 51000,'Supplier source correction property names must be exact.',1;
  IF EXISTS(SELECT 1 FROM @Nodes WHERE Name IN('sourceId','purchaseOrderId','supplierId','expectedConfigurationVersion','fundingItemId','debtItemId')
      AND (JsonType<>1 OR DATALENGTH(Value)<>72 OR TRY_CONVERT(uniqueidentifier,Value) IS NULL
        OR TRY_CONVERT(uniqueidentifier,Value)='00000000-0000-0000-0000-000000000000'))
    OR EXISTS(SELECT 1 FROM @Nodes WHERE Name IN('expectedSourceVersion','expectedPurchaseOrderVersion')
      AND (JsonType<>1 OR DATALENGTH(Value)<>36 OR TRY_CONVERT(binary(8),Value,1) IS NULL))
    OR EXISTS(SELECT 1 FROM @Nodes WHERE Name='expectedPlanFingerprint' AND
      (JsonType<>1 OR DATALENGTH(Value)<>128 OR TRY_CONVERT(binary(32),Value,2) IS NULL))
    OR EXISTS(SELECT 1 FROM @Nodes WHERE Name='postingDate' AND (JsonType<>1 OR DATALENGTH(Value)<>20
      OR TRY_CONVERT(date,Value,23) IS NULL OR CONVERT(nvarchar(10),TRY_CONVERT(date,Value,23),23)<>Value))
    OR EXISTS(SELECT 1 FROM @Nodes WHERE Name='currency' AND (JsonType<>1 OR DATALENGTH(Value)<>6 OR Value LIKE '%[^A-Z]%'))
    OR EXISTS(SELECT 1 FROM @Nodes WHERE Name='reason' AND (JsonType<>1 OR DATALENGTH(Value) NOT BETWEEN 2 AND 4000 OR LEN(TRIM(Value))=0))
    THROW 51000,'Invalid supplier correction identities, versions, date or reason.',1;
  IF (SELECT COUNT(*) FROM OPENJSON(@Command,'$.sourceDecisions'))>1000
    OR (SELECT COUNT(*) FROM OPENJSON(@Command,'$.reapplications'))>1000
    OR EXISTS(SELECT 1 FROM OPENJSON(@Command,'$.sourceDecisions') decision WHERE decision.type<>5
      OR (SELECT COUNT(*) FROM OPENJSON(decision.value))<>4 OR EXISTS(SELECT 1 FROM OPENJSON(decision.value) field WHERE
        field.[key] COLLATE Latin1_General_100_BIN2 NOT IN('sourceKind','sourceId','expectedSourceVersion','replacement')
        OR (field.[key]='replacement' AND field.type NOT IN(0,5)) OR (field.[key]<>'replacement' AND field.type<>1))
      OR JSON_VALUE(decision.value,'$.sourceKind') COLLATE Latin1_General_100_BIN2 NOT IN('SupplierCredit','SupplierBill','SupplierRefund','SupplierRefundRepayment','PurchaseRecognition'))
    OR EXISTS(SELECT 1 FROM OPENJSON(@Command,'$.sourceDecisions') GROUP BY JSON_VALUE(value,'$.sourceKind'),JSON_VALUE(value,'$.sourceId') HAVING COUNT(*)>1)
    OR EXISTS(SELECT 1 FROM OPENJSON(@Command,'$.reapplications') target WHERE target.type<>5
      OR (SELECT COUNT(*) FROM OPENJSON(target.value))<>3 OR EXISTS(SELECT 1 FROM OPENJSON(target.value) field WHERE field.type<>1
        OR field.[key] COLLATE Latin1_General_100_BIN2 NOT IN('fundingItemId','debtItemId','amount')))
    OR EXISTS(SELECT 1 FROM OPENJSON(@Command,'$.reapplications') GROUP BY JSON_VALUE(value,'$.fundingItemId'),JSON_VALUE(value,'$.debtItemId') HAVING COUNT(*)>1)
    OR EXISTS(SELECT 1 FROM OPENJSON(@Command,'$.reapplications') target CROSS APPLY OPENJSON(target.value) field WHERE field.[key]='amount'
      AND (DATALENGTH(field.value)<>DATALENGTH(RTRIM(field.value)) OR LEN(field.value) NOT BETWEEN 1 AND 29
        OR field.value COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9.]%' OR LEFT(field.value,1)='.' OR RIGHT(field.value,1)='.'
        OR LEN(field.value)-LEN(REPLACE(field.value,'.',''))>1 OR LEN(LEFT(field.value,CHARINDEX('.',field.value+'.')-1))>24
        OR LEN(SUBSTRING(field.value,CHARINDEX('.',field.value+'.')+1,100))>4
        OR TRY_CONVERT(decimal(28,4),field.value) IS NULL OR TRY_CONVERT(decimal(28,4),field.value)<=0))
    THROW 51000,'Invalid complete correction source decisions or reapplications.',1;
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
  IF DATALENGTH(@Canonical)>262144 THROW 51000,'Canonical supplier source correction exceeds its bound.',1;
END;
