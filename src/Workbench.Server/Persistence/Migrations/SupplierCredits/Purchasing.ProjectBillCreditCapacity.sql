-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.ProjectBillCreditCapacity
  @TenantId uniqueidentifier,@Canonical nvarchar(max),@Units nvarchar(max),@Credits nvarchar(max),
  @Positions nvarchar(max) OUTPUT,@Adjustments nvarchar(max) OUTPUT,@Bindings nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  IF @@TRANCOUNT=0 OR @TenantId<>TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'))
    OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Projected credit capacity requires its complete bill owner transaction.',1;
  DECLARE @Bill uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.replacement.post.billId')),
    @Revision uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.replacement.post.revisionId')),
    @Payload nvarchar(max),@PostingDate date=TRY_CONVERT(date,JSON_VALUE(@Canonical,'$.postingDate'),23),
    @Po uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.purchaseOrderId')),
    @Supplier uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.supplierId'));
  SELECT @Payload=revision.Payload FROM Purchasing.SupplierBills bill WITH(UPDLOCK,HOLDLOCK)
    JOIN Purchasing.SupplierBillRevisions revision ON revision.TenantId=bill.TenantId AND revision.BillId=bill.Id AND revision.Id=bill.CurrentRevisionId
    WHERE bill.TenantId=@TenantId AND bill.Id=@Bill AND revision.Id=@Revision AND bill.State='Reviewed'
      AND bill.RowVersion=TRY_CONVERT(binary(8),JSON_VALUE(@Canonical,'$.replacement.post.billVersion'),1);
  DECLARE @Future TABLE(EventId uniqueidentifier PRIMARY KEY,UnitId uniqueidentifier,OriginalUnitId uniqueidentifier,Input nvarchar(max));
  INSERT @Future SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(binding.value,'$.binding.invoiceEventId')),
      TRY_CONVERT(uniqueidentifier,JSON_VALUE(binding.value,'$.binding.successorUnitId')),
      TRY_CONVERT(uniqueidentifier,JSON_VALUE(binding.value,'$.unitId')),unit.value
    FROM OPENJSON(@Units) binding JOIN OPENJSON(@Payload,'$.units') unit
      ON JSON_VALUE(unit.value,'$.unitId')=JSON_VALUE(binding.value,'$.binding.successorUnitId');
  DECLARE @Proposed TABLE(CreditId uniqueidentifier,EventId uniqueidentifier,ComponentKey nvarchar(200),UnitId uniqueidentifier,
    PostingDate date,Amount decimal(28,4),Capacity decimal(38,4),CostCapacity decimal(38,4),Treatment varchar(16),RoundingOwnerKey nvarchar(200));
  INSERT @Proposed SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.replacementId')),future.EventId,
    JSON_VALUE(claim.value,'$.componentKey'),future.UnitId,TRY_CONVERT(date,JSON_VALUE(source.value,'$.replacement.postingDate'),23),
    CONVERT(decimal(28,4),JSON_VALUE(claim.value,'$.amount')),
    CONVERT(decimal(28,4),JSON_VALUE(component.value,'$.amount'))+
      CASE WHEN ABS(COALESCE(assigned.Adjustment,0))<=999999999999999999999999.9999 THEN CONVERT(decimal(28,4),COALESCE(assigned.Adjustment,0)) END,cost.Amount,
    CASE WHEN recognition.Matched=1 THEN 'Expense' ELSE 'Prepayment' END,
    CASE WHEN JSON_VALUE(component.value,'$.kind')='Rounding' THEN JSON_VALUE(component.value,'$.assignedCostComponentKey') END
    FROM OPENJSON(@Credits) source CROSS APPLY OPENJSON(source.value,'$.replacement.claims') claim
    JOIN @Future future ON future.EventId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(claim.value,'$.recognitionEventId'))
    JOIN OPENJSON(@Payload,'$.units') unit ON JSON_VALUE(unit.value,'$.unitId')=CONVERT(nvarchar(36),future.UnitId)
    CROSS APPLY OPENJSON(future.Input,'$.components') component
    OUTER APPLY(SELECT SUM(CASE WHEN JSON_VALUE(value,'$.kind')='Discount' THEN -CONVERT(decimal(38,4),JSON_VALUE(value,'$.amount'))
      ELSE CONVERT(decimal(38,4),JSON_VALUE(value,'$.amount')) END) Adjustment FROM OPENJSON(future.Input,'$.components')
      WHERE JSON_VALUE(value,'$.assignedCostComponentKey') COLLATE Latin1_General_100_BIN2=JSON_VALUE(component.value,'$.componentKey')
        AND JSON_VALUE(value,'$.kind') IN('Discount','Rounding')) assigned
    CROSS APPLY(SELECT SUM(CASE WHEN JSON_VALUE(value,'$.kind')='Discount' THEN -CONVERT(decimal(38,4),JSON_VALUE(value,'$.amount'))
      WHEN JSON_VALUE(value,'$.kind')='RecoverableTax' THEN 0 ELSE CONVERT(decimal(38,4),JSON_VALUE(value,'$.amount')) END) Amount
      FROM OPENJSON(future.Input,'$.components')) cost
    CROSS APPLY(SELECT CASE WHEN EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.sourceDecisions') decision JOIN Purchasing.RecognitionSideEvents original
        ON original.TenantId=@TenantId AND original.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(decision.value,'$.sourceId'))
          AND original.UnitId=future.OriginalUnitId AND original.Side='Recognition'
      WHERE JSON_VALUE(decision.value,'$.sourceKind')='PurchaseRecognition' AND JSON_QUERY(decision.value,'$.replacement') IS NOT NULL)
      THEN 1 ELSE 0 END Matched) recognition
    WHERE (JSON_VALUE(future.Input,'$.classification')='Expense' OR JSON_VALUE(future.Input,'$.classification')='Inventory' AND recognition.Matched=0)
      AND JSON_VALUE(component.value,'$.componentKey') COLLATE Latin1_General_100_BIN2=JSON_VALUE(claim.value,'$.componentKey')
      AND JSON_VALUE(component.value,'$.kind') IN('BaseCost','Freight','Charge','NonrecoverableTax','Rounding')
      AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(claim.value,'$.billId'))=@Bill
      AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(claim.value,'$.billRevisionId'))=@Revision;
  IF (SELECT COUNT(*) FROM @Proposed)<>(SELECT COUNT(*) FROM OPENJSON(@Credits) source CROSS APPLY OPENJSON(source.value,'$.replacement.claims') claim
      JOIN @Future future ON future.EventId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(claim.value,'$.recognitionEventId')))
    OR EXISTS(SELECT 1 FROM @Proposed WHERE PostingDate<@PostingDate OR Capacity IS NULL OR Capacity<0 OR CostCapacity<0
      OR Capacity>999999999999999999999999.9999 OR CostCapacity>999999999999999999999999.9999)
    THROW 51004,'Future credit claims require exact reviewed successor cost provenance and eligible dates.',1;
  IF EXISTS(SELECT EventId,ComponentKey FROM @Proposed GROUP BY EventId,ComponentKey
      HAVING SUM(CONVERT(decimal(38,4),Amount))>MIN(Capacity))
    OR EXISTS(SELECT UnitId FROM @Proposed GROUP BY UnitId HAVING SUM(CONVERT(decimal(38,4),Amount))>MIN(CostCapacity))
    THROW 51009,'The complete proposal exceeds reviewed successor component or whole-unit cost capacity.',1;
  IF EXISTS(SELECT rounded.EventId,rounded.ComponentKey FROM @Proposed rounded JOIN @Proposed assigned
    ON assigned.EventId=rounded.EventId AND assigned.ComponentKey=rounded.RoundingOwnerKey
    GROUP BY rounded.EventId,rounded.ComponentKey,rounded.RoundingOwnerKey
    HAVING (SELECT SUM(CONVERT(decimal(38,4),claim.Amount)) FROM @Proposed claim
      WHERE claim.EventId=rounded.EventId AND claim.ComponentKey IN(rounded.ComponentKey,rounded.RoundingOwnerKey))>MIN(assigned.Capacity))
    THROW 51009,'The complete proposal exceeds reviewed successor assigned rounding capacity.',1;
  DECLARE @ExistingSources TABLE(Id uniqueidentifier PRIMARY KEY,Input nvarchar(max));
  DECLARE @ExistingClaims TABLE(Position int PRIMARY KEY,Input nvarchar(max));
  DECLARE @Id uniqueidentifier,@Source nvarchar(max),@Claims nvarchar(max),@Replacement nvarchar(max),@Filtered nvarchar(max);
  DECLARE sources CURSOR LOCAL FAST_FORWARD FOR SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.sourceId')),value FROM OPENJSON(@Credits)
    ORDER BY JSON_VALUE(value,'$.sourceId');
  OPEN sources; FETCH NEXT FROM sources INTO @Id,@Source;
  WHILE @@FETCH_STATUS=0
  BEGIN
    SET @Replacement=JSON_QUERY(@Source,'$.replacement');
    IF @Replacement IS NOT NULL
    BEGIN
      DELETE @ExistingClaims;
      INSERT @ExistingClaims SELECT CONVERT(int,claim.[key]),claim.value FROM OPENJSON(@Replacement,'$.claims') claim
        WHERE NOT EXISTS(SELECT 1 FROM @Future future WHERE future.EventId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(claim.value,'$.recognitionEventId')));
      IF EXISTS(SELECT 1 FROM @ExistingClaims claim WHERE NOT EXISTS(
        SELECT 1 FROM Purchasing.SupplierBillPostings bill JOIN Purchasing.SupplierBills owner
          ON owner.TenantId=bill.TenantId AND owner.Id=bill.BillId AND owner.State='Posted'
            AND owner.SupplierId=@Supplier AND owner.PurchaseOrderId=@Po AND owner.Currency=JSON_VALUE(@Canonical,'$.currency')
        JOIN Purchasing.SupplierBillPostingEvents owned ON owned.TenantId=bill.TenantId AND owned.BillId=bill.BillId
          AND owned.EventId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(claim.Input,'$.recognitionEventId'))
        WHERE bill.TenantId=@TenantId AND bill.BillId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(claim.Input,'$.billId'))
          AND bill.RevisionId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(claim.Input,'$.billRevisionId')))
        OR EXISTS(SELECT 1 FROM OPENJSON(@Units) unit CROSS APPLY OPENJSON(unit.value,'$.originals') original
          WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(original.value,'$.eventId'))=TRY_CONVERT(uniqueidentifier,JSON_VALUE(claim.Input,'$.recognitionEventId'))))
        THROW 51004,'An existing credit claim requires its surviving authentic bill provenance.',1;
      -- Capacity calculation consumes only surviving existing-event claims. Future claims
      -- are validated above against the real reviewed payload; no financial source is invented.
      SET @Filtered=(SELECT N'['+COALESCE(STRING_AGG(CONVERT(nvarchar(max),Input),N',') WITHIN GROUP(ORDER BY Position),N'')+N']' FROM @ExistingClaims);
      SET @Replacement=JSON_MODIFY(@Replacement,'$.claims',JSON_QUERY(@Filtered));
      SET @Source=JSON_MODIFY(@Source,'$.replacement',JSON_QUERY(@Replacement));
    END;
    INSERT @ExistingSources VALUES(@Id,@Source);
    FETCH NEXT FROM sources INTO @Id,@Source;
  END;
  CLOSE sources; DEALLOCATE sources;
  DECLARE @ExistingJson nvarchar(max)=(SELECT N'['+COALESCE(STRING_AGG(CONVERT(nvarchar(max),Input),N',') WITHIN GROUP(ORDER BY Id),N'')+N']' FROM @ExistingSources),
    @ExistingPositions nvarchar(max),@AllAdjustments nvarchar(max);
  EXEC Purchasing.PlanSupplierCreditCapacity @TenantId,@ExistingJson,@ExistingPositions OUTPUT,@AllAdjustments OUTPUT;
  EXEC Purchasing.PlanSupplierCreditPositionBindings @TenantId,@ExistingJson,@Bindings OUTPUT;
  DECLARE @Unaffected TABLE(Position int PRIMARY KEY,Input nvarchar(max));
  INSERT @Unaffected SELECT CONVERT(int,adjustment.[key]),adjustment.value FROM OPENJSON(@AllAdjustments) adjustment
    WHERE NOT EXISTS(SELECT 1 FROM OPENJSON(@Units) unit CROSS APPLY OPENJSON(unit.value,'$.originals') original
      WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(original.value,'$.eventId'))=TRY_CONVERT(uniqueidentifier,JSON_VALUE(adjustment.value,'$.invoiceEventId')));
  SET @Adjustments=(SELECT N'['+COALESCE(STRING_AGG(CONVERT(nvarchar(max),Input),N',') WITHIN GROUP(ORDER BY Position),N'')+N']' FROM @Unaffected);
  DECLARE @FuturePositions nvarchar(max)=(SELECT EventId recognitionEventId,ComponentKey componentKey,PostingDate postingDate,
    CONVERT(nvarchar(60),SUM(CONVERT(decimal(38,4),Amount))) claimed,CONVERT(nvarchar(60),MIN(Capacity)) capacity,
    CONVERT(nvarchar(60),CONVERT(decimal(28,4),MIN(Capacity))-CONVERT(decimal(28,4),SUM(CONVERT(decimal(38,4),Amount)))) remaining
    FROM @Proposed GROUP BY EventId,ComponentKey,PostingDate ORDER BY EventId,ComponentKey,PostingDate FOR JSON PATH);
  SET @Positions=LEFT(@ExistingPositions,LEN(@ExistingPositions)-1)+CASE WHEN @ExistingPositions<>N'[]' AND @FuturePositions<>N'[]' THEN N',' ELSE N'' END+SUBSTRING(@FuturePositions,2,LEN(@FuturePositions));
END;
