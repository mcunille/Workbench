-- Copyright (c) 2026 The White Stag Collection.
CREATE FUNCTION Purchasing.SupplierCashTargetControl(@TenantId uniqueidentifier,@Kind varchar(32),@ItemId uniqueidentifier)
RETURNS TABLE AS RETURN
  SELECT AccountId,AccountVersion,AccountCode,AccountName,AccountType,AccountPurpose
    FROM Purchasing.SupplierAssetControl(@TenantId,@ItemId) WHERE @Kind='SupplierRefund'
  UNION ALL
  SELECT AccountId,AccountVersion,AccountCode,AccountName,AccountType,AccountPurpose
    FROM Purchasing.SupplierRefundReleaseControl(@TenantId,@ItemId) WHERE @Kind='SupplierRefundRepayment';
