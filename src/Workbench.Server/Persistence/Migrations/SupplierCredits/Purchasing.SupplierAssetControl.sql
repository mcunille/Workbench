-- Copyright (c) 2026 The White Stag Collection.
CREATE FUNCTION Purchasing.SupplierAssetControl(@TenantId uniqueidentifier,@ItemId uniqueidentifier)
RETURNS TABLE AS RETURN
  SELECT AccountId,AccountVersion,AccountCode,AccountName,AccountType,AccountPurpose FROM Purchasing.SupplierPaymentControl(@TenantId,@ItemId)
  UNION ALL
  SELECT AccountId,AccountVersion,AccountCode,AccountName,AccountType,AccountPurpose FROM Purchasing.SupplierCreditControl(@TenantId,@ItemId);
