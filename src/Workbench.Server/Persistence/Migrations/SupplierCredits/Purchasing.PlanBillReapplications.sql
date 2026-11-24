-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.PlanBillReapplications
  @TenantId uniqueidentifier,@Canonical nvarchar(max),@Sources nvarchar(max),@Reapplications nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId<>TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'))
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Reapplication planning requires its complete bill owner transaction.',1;
  DECLARE @Po uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.purchaseOrderId')),
    @Supplier uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.supplierId')),
    @Bill uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.sourceId')),
    @Currency varchar(3)=JSON_VALUE(@Canonical,'$.currency'),@Date date=CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23),@Scale int;
  SELECT @Scale=TRY_CONVERT(int,JSON_VALUE(Payload,'$.policies.scale')) FROM Accounting.Configurations
    WHERE TenantId=@TenantId AND Version=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.expectedConfigurationVersion'));
  DECLARE @Targets TABLE(Position int PRIMARY KEY,FundingId uniqueidentifier,DebtId uniqueidentifier,Amount decimal(28,4),PostingDate date);
  INSERT @Targets SELECT CONVERT(int,[key]),CONVERT(uniqueidentifier,JSON_VALUE(value,'$.fundingItemId')),
    CONVERT(uniqueidentifier,JSON_VALUE(value,'$.debtItemId')),CONVERT(decimal(28,4),JSON_VALUE(value,'$.amount')),@Date
    FROM OPENJSON(@Canonical,'$.reapplications');
  INSERT @Targets SELECT (SELECT COUNT(*) FROM OPENJSON(@Canonical,'$.reapplications'))
    +ROW_NUMBER() OVER(ORDER BY JSON_VALUE(source.value,'$.sourceId'),CONVERT(int,target.[key]))-1,
    CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.replacementId')),CONVERT(uniqueidentifier,JSON_VALUE(target.value,'$.itemId')),
    CONVERT(decimal(28,4),JSON_VALUE(target.value,'$.amount')),@Date
    FROM OPENJSON(@Sources) source CROSS APPLY OPENJSON(source.value,'$.requestedAllocations') target;
  IF EXISTS(SELECT FundingId,DebtId FROM @Targets GROUP BY FundingId,DebtId HAVING COUNT(*)>1)
    OR EXISTS(SELECT 1 FROM @Targets WHERE Amount<>ROUND(Amount,@Scale,1))
    THROW 51000,'Reapplications require distinct targets and exact configured precision.',1;
  IF EXISTS(SELECT 1 FROM OPENJSON(@Sources) source CROSS APPLY OPENJSON(source.value,'$.requestedAllocations') target
    WHERE NOT EXISTS(SELECT 1 FROM Purchasing.SupplierOpenItems item
      JOIN Purchasing.SupplierItemVersions version WITH(UPDLOCK,HOLDLOCK) ON version.TenantId=item.TenantId AND version.ItemId=item.Id
      JOIN Purchasing.SupplierBillPostings bill ON bill.TenantId=item.TenantId AND bill.BillId=item.BillId AND bill.RevisionId=item.SourceRevisionId
      WHERE item.TenantId=@TenantId AND item.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(target.value,'$.itemId'))
        AND item.BillId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(target.value,'$.billId')) AND item.Kind='Payable'
        AND item.SupplierId=@Supplier AND item.PurchaseOrderId=@Po AND item.Currency=@Currency
        AND version.RowVersion=TRY_CONVERT(binary(8),JSON_VALUE(target.value,'$.expectedItemVersion'),1)))
    THROW 51009,'A credit successor allocation target observation changed.',1;
  IF EXISTS(SELECT 1 FROM @Targets target WHERE NOT EXISTS(SELECT 1 FROM OPENJSON(@Sources) source
      WHERE JSON_VALUE(source.value,'$.sourceKind')='SupplierCredit' AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.replacementId'))=target.FundingId)
    AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierOpenItems item CROSS APPLY Purchasing.SupplierItemControl(@TenantId,item.Id) control
      WHERE item.TenantId=@TenantId AND item.Id=target.FundingId AND item.Kind IN('Advance','CreditReceivable')
        AND item.SupplierId=@Supplier AND item.PurchaseOrderId=@Po AND item.Currency=@Currency))
    THROW 51004,'Reapplication requires an authentic existing or explicitly planned funding source.',1;
  IF EXISTS(SELECT 1 FROM @Targets target WHERE NOT EXISTS(SELECT 1 FROM OPENJSON(@Sources) source
      WHERE JSON_VALUE(source.value,'$.sourceKind')='SupplierBill' AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.replacementId'))=target.DebtId)
    AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierCashSettlements settlement
      JOIN Purchasing.SupplierCashSources cash ON cash.TenantId=settlement.TenantId AND cash.Id=settlement.CashSourceId AND cash.Kind='SupplierRefund'
      CROSS APPLY Purchasing.SupplierCashControl(@TenantId,cash.Id) control
      JOIN OPENJSON(@Sources) source ON JSON_VALUE(source.value,'$.sourceKind')='SupplierCredit'
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.sourceId'))=settlement.ItemId
      WHERE settlement.TenantId=@TenantId AND settlement.Id=target.DebtId
        AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierCashSettlementReleases released WHERE released.TenantId=settlement.TenantId AND released.SettlementId=settlement.Id)
        AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierSourceCorrections corrected WHERE corrected.TenantId=cash.TenantId AND corrected.SourceKind=cash.Kind AND corrected.SourceId=cash.Id))
    AND NOT EXISTS(SELECT 1 FROM Purchasing.SupplierOpenItems item CROSS APPLY Purchasing.SupplierItemControl(@TenantId,item.Id) control
      WHERE item.TenantId=@TenantId AND item.Id=target.DebtId AND item.Id<>@Bill AND item.Kind IN('Payable','RefundClearing')
        AND item.SupplierId=@Supplier AND item.PurchaseOrderId=@Po AND item.Currency=@Currency))
    THROW 51004,'Reapplication requires authentic existing or explicitly planned debt.',1;
  UPDATE target SET PostingDate=(SELECT MAX(dateValue) FROM(
    SELECT @Date dateValue UNION ALL SELECT item.SourcePostingDate FROM Purchasing.SupplierOpenItems item
      WHERE item.TenantId=@TenantId AND item.Id IN(target.FundingId,target.DebtId)
    UNION ALL SELECT TRY_CONVERT(date,JSON_VALUE(source.value,'$.replacement.postingDate'),23) FROM OPENJSON(@Sources) source
      WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.replacementId'))=target.FundingId
  ) dates) FROM @Targets target;
  SET @Reapplications=(SELECT FundingId fundingItemId,DebtId debtItemId,CONVERT(nvarchar(60),Amount) amount,PostingDate postingDate
    FROM @Targets ORDER BY Position FOR JSON PATH);
END;
