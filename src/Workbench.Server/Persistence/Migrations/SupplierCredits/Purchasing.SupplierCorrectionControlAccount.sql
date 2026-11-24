-- Copyright (c) 2026 The White Stag Collection.
CREATE FUNCTION Purchasing.SupplierCorrectionControlAccount(
  @TenantId uniqueidentifier,@Policy nvarchar(max),@Slot varchar(40),@Type varchar(16))
RETURNS TABLE AS RETURN
  SELECT account.Id AccountId,account.Version AccountVersion FROM OPENJSON(@Policy,'$.mappings')
    WITH(Slot varchar(40) '$.slot',AccountId nvarchar(4000) '$.accountId') mapping
  JOIN Accounting.Accounts account WITH(UPDLOCK,HOLDLOCK)
    ON account.TenantId=@TenantId AND account.Id=TRY_CONVERT(uniqueidentifier,mapping.AccountId)
    AND account.Type=@Type AND account.Purpose=@Slot AND account.ArchivedAtUtc IS NULL
  WHERE mapping.Slot=@Slot;
