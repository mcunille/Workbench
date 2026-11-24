-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.PlanBillRecognitionSources
  @TenantId uniqueidentifier,@ActorId uniqueidentifier,@SessionId uniqueidentifier,@Canonical nvarchar(max),@Units nvarchar(max),
  @Owners nvarchar(max) OUTPUT,@Successors nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Sources TABLE(EventId uniqueidentifier PRIMARY KEY,UnitId uniqueidentifier,Side varchar(16),EventRevision int,SourceId uniqueidentifier,
    Replacement nvarchar(max),Facts nvarchar(max));
  INSERT @Sources SELECT actual.Id,actual.UnitId,actual.Side,actual.EventRevision,actual.SourceId,JSON_QUERY(decision.value,'$.replacement'),NULL
    FROM OPENJSON(@Units) unit CROSS APPLY OPENJSON(unit.value,'$.originals') original
    JOIN Purchasing.RecognitionSideEvents actual ON actual.TenantId=@TenantId AND actual.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(original.value,'$.eventId'))
    OUTER APPLY(SELECT value FROM OPENJSON(@Canonical,'$.sourceDecisions') WHERE JSON_VALUE(value,'$.sourceKind')='PurchaseRecognition'
      AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.sourceId'))=actual.Id) decision;
  IF EXISTS(SELECT 1 FROM @Sources source WHERE source.Side<>'Invoice' AND NOT EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.sourceDecisions') decision
      WHERE JSON_VALUE(decision.value,'$.sourceKind')='PurchaseRecognition' AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(decision.value,'$.sourceId'))=source.EventId
        AND TRY_CONVERT(binary(8),JSON_VALUE(decision.value,'$.expectedSourceVersion'),1)=CONVERT(binary(8),source.EventRevision)))
    OR EXISTS(SELECT 1 FROM OPENJSON(@Canonical,'$.sourceDecisions') decision WHERE JSON_VALUE(decision.value,'$.sourceKind')='PurchaseRecognition'
      AND NOT EXISTS(SELECT 1 FROM @Sources source WHERE source.Side<>'Invoice' AND source.EventId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(decision.value,'$.sourceId'))))
    THROW 51009,'Every matched recognition side requires its explicit current source decision.',1;
  DECLARE @Event uniqueidentifier,@Replacement nvarchar(max),@Facts nvarchar(max);
  DECLARE sources CURSOR LOCAL FAST_FORWARD FOR SELECT EventId,Replacement FROM @Sources ORDER BY SourceId,EventId;
  OPEN sources; FETCH NEXT FROM sources INTO @Event,@Replacement;
  WHILE @@FETCH_STATUS=0
  BEGIN
    EXEC Purchasing.ValidateRecognitionSourceOwner @TenantId,@ActorId,@SessionId,@Event,@Replacement,@Facts OUTPUT;
    UPDATE @Sources SET Facts=@Facts WHERE EventId=@Event;
    FETCH NEXT FROM sources INTO @Event,@Replacement;
  END;
  CLOSE sources; DEALLOCATE sources;
  IF EXISTS(SELECT 1 FROM @Sources source WHERE source.Replacement IS NOT NULL AND NOT EXISTS(
    SELECT 1 FROM Purchasing.RecognitionSideEvents original JOIN Purchasing.RecognitionUnits unit
      ON unit.TenantId=original.TenantId AND unit.Id=original.UnitId
    WHERE original.TenantId=@TenantId AND original.Id=source.EventId
      AND JSON_VALUE(source.Facts,'$.facts.successorSide.side') COLLATE Latin1_General_100_BIN2=original.Side
      AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.Facts,'$.facts.successorSide.sourceId'))=original.SourceId
      AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.Facts,'$.facts.successorSide.sourceRevision'))=TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.Replacement,'$.sourceRevision'))
      AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.Replacement,'$.sourceRevision'))<>original.SourceRevision
      AND CONVERT(varbinary(max),JSON_VALUE(source.Facts,'$.facts.successorSide.sourceComponentKey'))=CONVERT(varbinary(max),original.SourceComponentKey)
      AND CONVERT(varbinary(max),JSON_VALUE(source.Facts,'$.facts.successorSide.subdivisionKey'))=CONVERT(varbinary(max),original.SubdivisionKey)
      AND TRY_CONVERT(date,JSON_VALUE(source.Facts,'$.facts.successorSide.effectiveDate'),23)=original.EffectiveDate
      AND TRY_CONVERT(decimal(28,6),JSON_VALUE(source.Facts,'$.facts.successorSide.sourceQuantity'))=original.SourceQuantity
      AND original.SourceQuantity=unit.Quantity))
    THROW 51009,'The complete producer successor must preserve original source identity, whole quantity and effective date.',1;
  DECLARE @BillPayload nvarchar(max);
  SELECT @BillPayload=revision.Payload FROM Purchasing.SupplierBillRevisions revision
    WHERE revision.TenantId=@TenantId AND revision.BillId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.replacement.post.billId'))
      AND revision.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.replacement.post.revisionId'));
  IF EXISTS(SELECT 1 FROM @Sources source JOIN Purchasing.RecognitionUnits unit ON unit.TenantId=@TenantId AND unit.Id=source.UnitId
      AND unit.Classification='Inventory'
    CROSS APPLY(SELECT SUM(CASE WHEN component.Kind='Discount' THEN -CONVERT(decimal(38,4),component.Amount)
        WHEN component.Kind='RecoverableTax' THEN 0 ELSE CONVERT(decimal(38,4),component.Amount) END) Amount
      FROM @Sources invoice JOIN Purchasing.RecognitionComponents component ON component.TenantId=@TenantId AND component.EventId=invoice.EventId
      WHERE invoice.UnitId=source.UnitId AND invoice.Side='Invoice') originalCost
    OUTER APPLY(SELECT SUM(CASE WHEN JSON_VALUE(component.value,'$.kind')='Discount' THEN -CONVERT(decimal(38,4),JSON_VALUE(component.value,'$.amount'))
        WHEN JSON_VALUE(component.value,'$.kind')='RecoverableTax' THEN 0 ELSE CONVERT(decimal(38,4),JSON_VALUE(component.value,'$.amount')) END) Amount
      FROM OPENJSON(@Units) binding JOIN OPENJSON(@BillPayload,'$.units') successor
        ON JSON_VALUE(successor.value,'$.unitId')=JSON_VALUE(binding.value,'$.binding.successorUnitId')
      CROSS APPLY OPENJSON(successor.value,'$.components') component
      WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(binding.value,'$.unitId'))=source.UnitId) successorCost
    OUTER APPLY(SELECT SUM(CASE WHEN JSON_VALUE(component.value,'$.kind')='Discount' THEN -CONVERT(decimal(38,4),JSON_VALUE(component.value,'$.amount'))
        WHEN JSON_VALUE(component.value,'$.kind')='RecoverableTax' THEN 0 ELSE CONVERT(decimal(38,4),JSON_VALUE(component.value,'$.amount')) END) Amount
      FROM OPENJSON(source.Facts,'$.facts.successorSide.components') component WHERE @BillPayload IS NULL) receiptCost
    WHERE source.Side='Recognition' AND originalCost.Amount<>COALESCE(successorCost.Amount,receiptCost.Amount,0)
      AND COALESCE(JSON_VALUE(source.Facts,'$.facts.successorSide.evidence.inventoryAdjustmentState'),'') COLLATE Latin1_General_100_BIN2<>'Held')
    THROW 51004,'An inventory cost correction requires current held-state proof from its complete source producer.',1;
  IF EXISTS(SELECT 1 FROM @Sources source WHERE source.Replacement IS NOT NULL AND
      ((SELECT COUNT(*) FROM OPENJSON(source.Replacement))<>3 OR EXISTS(SELECT 1 FROM OPENJSON(source.Replacement) field
        WHERE field.[key] COLLATE Latin1_General_100_BIN2 NOT IN('sourceRevision','successorUnitId','recognitionEventId')
          OR field.type<>1 OR DATALENGTH(field.value)<>72 OR TRY_CONVERT(uniqueidentifier,field.value) IS NULL
          OR TRY_CONVERT(uniqueidentifier,field.value)='00000000-0000-0000-0000-000000000000')
      OR NOT EXISTS(SELECT 1 FROM OPENJSON(@Units) unit WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(unit.value,'$.unitId'))=source.UnitId
        AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(unit.value,'$.binding.successorUnitId'))=TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.Replacement,'$.successorUnitId')))
      OR JSON_QUERY(source.Facts,'$.facts.successorSide') IS NULL
      OR EXISTS(SELECT 1 FROM Purchasing.RecognitionSideEvents WHERE TenantId=@TenantId AND Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.Replacement,'$.recognitionEventId')))))
    THROW 51004,'Complete genuine upstream successor facts and fresh whole-unit bindings are required.',1;
  SET @Owners=(SELECT EventId eventId,UnitId unitId,Side side,EventRevision eventRevision,JSON_QUERY(Replacement) replacement,
    JSON_QUERY(Facts) currentOwner FROM @Sources ORDER BY EventId FOR JSON PATH,INCLUDE_NULL_VALUES);
  SET @Successors=(SELECT source.EventId originalEventId,source.UnitId originalUnitId,
    TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.Replacement,'$.successorUnitId')) unitId,
    TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.Replacement,'$.recognitionEventId')) eventId,
    JSON_QUERY(JSON_MODIFY(JSON_QUERY(source.Facts,'$.facts.successorSide'),'$.eventRevision',source.EventRevision+1)) side
    FROM @Sources source WHERE source.Replacement IS NOT NULL ORDER BY source.EventId FOR JSON PATH);
  DECLARE @SuccessorRows TABLE(Position int PRIMARY KEY,Input nvarchar(max));
  INSERT @SuccessorRows SELECT CONVERT(int,[key]),value FROM OPENJSON(@Successors);
  DECLARE @Position int,@Input nvarchar(max),@UnitInput nvarchar(max),@Post nvarchar(max),@PostCanonical nvarchar(max);
  DECLARE successors CURSOR LOCAL FAST_FORWARD FOR SELECT Position,Input FROM @SuccessorRows ORDER BY Position;
  OPEN successors; FETCH NEXT FROM successors INTO @Position,@Input;
  WHILE @@FETCH_STATUS=0
  BEGIN
    SELECT @UnitInput=(SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Input,'$.unitId')) unitId,unit.Classification classification,
      unit.GoodsReference goodsReference,CONVERT(nvarchar(60),unit.Quantity) quantity,unit.QuantityUnit quantityUnit,
      CASE WHEN @BillPayload IS NULL THEN 0 ELSE 1 END expectedPriorEventRevision,
      JSON_QUERY(N'['+JSON_QUERY(@Input,'$.side')+N']') sides FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)
      FROM Purchasing.RecognitionUnits unit WHERE unit.TenantId=@TenantId AND unit.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Input,'$.originalUnitId'));
    SET @Post=(SELECT 1 schemaVersion,'Post' operation,JSON_VALUE(@Canonical,'$.expectedConfigurationVersion') expectedConfigurationVersion,
      JSON_VALUE(@Canonical,'$.purchaseOrderId') purchaseOrderId,JSON_VALUE(@Canonical,'$.expectedPurchaseOrderVersion') expectedPurchaseOrderVersion,
      JSON_VALUE(@Canonical,'$.supplierId') supplierId,JSON_VALUE(@Canonical,'$.currency') currency,JSON_VALUE(@Canonical,'$.postingDate') postingDate,
      JSON_QUERY(N'['+@UnitInput+N']') units FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    EXEC Purchasing.CanonicalizeRecognition @Post,0,@PostCanonical OUTPUT;
    UPDATE @SuccessorRows SET Input=JSON_MODIFY(Input,'$.command',JSON_QUERY(@PostCanonical)) WHERE Position=@Position;
    FETCH NEXT FROM successors INTO @Position,@Input;
  END;
  CLOSE successors; DEALLOCATE successors;
  SET @Successors=(SELECT N'['+COALESCE(STRING_AGG(CONVERT(nvarchar(max),Input),N',') WITHIN GROUP(ORDER BY Position),N'')+N']' FROM @SuccessorRows);
END;
