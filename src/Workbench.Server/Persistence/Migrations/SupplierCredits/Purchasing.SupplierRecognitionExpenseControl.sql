-- Copyright (c) 2026 The White Stag Collection.
CREATE FUNCTION Purchasing.SupplierRecognitionExpenseControl(@TenantId uniqueidentifier,@RecognitionEventId uniqueidentifier)
RETURNS TABLE AS RETURN
  SELECT line.AccountId,line.AccountVersion,line.AccountCode,line.AccountName,line.AccountType,line.AccountPurpose,
    line.JournalId,line.Ordinal
  FROM Purchasing.ActiveRecognitionSideEvents recognition
  JOIN Purchasing.RecognitionUnits unit ON unit.TenantId=recognition.TenantId AND unit.Id=recognition.UnitId AND unit.Classification='Expense'
  JOIN Accounting.JournalLines line ON line.TenantId=recognition.TenantId AND line.JournalId=recognition.JournalId
    AND line.AccountType='Expense' AND line.AccountPurpose='General' AND line.Debit>0 AND line.Credit=0
  WHERE recognition.TenantId=@TenantId AND recognition.Id=@RecognitionEventId AND recognition.Side='Recognition'
  UNION ALL
  -- Zero-only recognition still records SQL-owned account mappings. The accounting revision
  -- supplies the exact historical label/version without inventing a journal or changing quantity.
  SELECT revision.AccountId,revision.Version,
    CONVERT(nvarchar(32),JSON_VALUE(revision.Payload,'$.code')) COLLATE Latin1_General_100_BIN2,
    CONVERT(nvarchar(160),JSON_VALUE(revision.Payload,'$.name')),
    CONVERT(nvarchar(20),JSON_VALUE(revision.Payload,'$.type')),
    CONVERT(nvarchar(40),JSON_VALUE(revision.Payload,'$.purpose')),
    CONVERT(uniqueidentifier,NULL),CONVERT(int,NULL)
  FROM Purchasing.ActiveRecognitionSideEvents recognition
  JOIN Purchasing.RecognitionUnits unit ON unit.TenantId=recognition.TenantId AND unit.Id=recognition.UnitId AND unit.Classification='Expense'
  CROSS APPLY OPENJSON(recognition.EvidenceJson,'$.accountMappings') mapping
  JOIN Accounting.Revisions revision ON revision.TenantId=recognition.TenantId
    AND revision.AccountId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(mapping.value,'$.accountId'))
    AND revision.Version=TRY_CONVERT(uniqueidentifier,JSON_VALUE(mapping.value,'$.accountVersion'))
  WHERE recognition.TenantId=@TenantId AND recognition.Id=@RecognitionEventId AND recognition.Side='Recognition'
    AND recognition.JournalId IS NULL AND recognition.SourceAmount=0
    AND JSON_VALUE(mapping.value,'$.slot')='Expense'
    AND JSON_VALUE(revision.Payload,'$.type')='Expense' AND JSON_VALUE(revision.Payload,'$.purpose')='General';
