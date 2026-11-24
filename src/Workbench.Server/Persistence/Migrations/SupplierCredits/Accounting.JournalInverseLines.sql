-- Copyright (c) 2026 The White Stag Collection.
CREATE FUNCTION Accounting.JournalInverseLines(@TenantId uniqueidentifier,@OriginalJournalId uniqueidentifier)
RETURNS TABLE AS RETURN
  SELECT TenantId,Ordinal,AccountId,AccountVersion,AccountCode,AccountName,AccountType,AccountPurpose,
    Credit Debit,Debit Credit
  FROM Accounting.JournalLines WHERE TenantId=@TenantId AND JournalId=@OriginalJournalId;
