-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.PlanSupplierBillJournals
  @TenantId uniqueidentifier,@Date date,@Currency varchar(3),@Policy nvarchar(max),
  @PositionBindings nvarchar(max) OUTPUT,@ProjectedControls nvarchar(max) OUTPUT,@Plan nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Bill uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Plan,'$.sources[0].replacement.post.billId')),
    @BillRevision uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Plan,'$.sources[0].replacement.post.revisionId')),@Payload nvarchar(max);
  SELECT @Payload=Payload FROM Purchasing.SupplierBillRevisions WHERE TenantId=@TenantId AND BillId=@Bill AND Id=@BillRevision;

  DECLARE @Sides TABLE(EventId uniqueidentifier PRIMARY KEY,UnitId uniqueidentifier,Side varchar(16),
    SourceRevision uniqueidentifier,Classification varchar(16),Components nvarchar(max));
  INSERT @Sides SELECT binding.EventId,binding.UnitId,'Invoice',@BillRevision,unit.Classification,unit.Components
    FROM OPENJSON(@Plan,'$.units') WITH(EventId uniqueidentifier '$.binding.invoiceEventId',UnitId uniqueidentifier '$.binding.successorUnitId') binding
    JOIN OPENJSON(@Payload,'$.units') WITH(UnitId uniqueidentifier '$.unitId',Classification varchar(16) '$.classification',
      Components nvarchar(max) '$.components' AS JSON) unit ON unit.UnitId=binding.UnitId;
  INSERT @Sides SELECT EventId,UnitId,'Recognition',SourceRevision,Classification,Components
    FROM OPENJSON(@Plan,'$.recognitionSuccessors') WITH(EventId uniqueidentifier '$.eventId',UnitId uniqueidentifier '$.unitId',
      SourceRevision uniqueidentifier '$.side.sourceRevision',Classification varchar(16) '$.command.units[0].classification',
      Components nvarchar(max) '$.side.components' AS JSON);
  DECLARE @Resolved TABLE(EventId uniqueidentifier PRIMARY KEY,UnitId uniqueidentifier,Side varchar(16),SourceRevision uniqueidentifier,
    Cost decimal(28,4),Mappings nvarchar(max),Lines nvarchar(max));
  DECLARE @Event uniqueidentifier,@Unit uniqueidentifier,@Side varchar(16),@Revision uniqueidentifier,@Classification varchar(16),@Components nvarchar(max),
    @Cost decimal(28,4),@Tax decimal(28,4),@HasPrior bit,@PriorCost decimal(28,4),@PriorMappings nvarchar(max),
    @Mappings nvarchar(max),@Lines nvarchar(max);
  -- Bill invoices post first. Any explicitly retained receipt then consumes the same planned prepayment.
  DECLARE sides CURSOR LOCAL FAST_FORWARD FOR SELECT EventId,UnitId,Side,SourceRevision,Classification,Components FROM @Sides ORDER BY Side,EventId;
  OPEN sides; FETCH NEXT FROM sides INTO @Event,@Unit,@Side,@Revision,@Classification,@Components;
  WHILE @@FETCH_STATUS=0
  BEGIN
    SELECT @Cost=Cost,@Tax=Tax FROM Purchasing.RecognitionComponentTotals(@Components);
    SET @HasPrior=0; SET @PriorCost=NULL; SET @PriorMappings=NULL;
    SELECT @HasPrior=1,@PriorCost=Cost,@PriorMappings=Mappings FROM @Resolved WHERE UnitId=@Unit AND Side='Invoice';
    EXEC Purchasing.BuildSupplierRecognitionJournalLines @TenantId,@Policy,@Side,@Classification,@Cost,@Tax,@HasPrior,@PriorCost,
      @PriorMappings,@Mappings OUTPUT,@Lines OUTPUT;
    INSERT @Resolved VALUES(@Event,@Unit,@Side,@Revision,@Cost,@Mappings,@Lines);
    FETCH NEXT FROM sides INTO @Event,@Unit,@Side,@Revision,@Classification,@Components;
  END;
  CLOSE sides; DEALLOCATE sides;

  DECLARE @Journals nvarchar(max)=(SELECT 'Replacement' memberKind,'PurchaseRecognition' sourceKind,EventId sourceId,
    SourceRevision sourceRevisionId,CONVERT(uniqueidentifier,NULL) originalJournalId,EventId ownerMemberId,CONVERT(int,NULL) position,
    @Date postingDate,@Currency currency,JSON_QUERY(Lines) lines FROM @Resolved WHERE Lines<>N'[]' FOR JSON PATH,INCLUDE_NULL_VALUES);
  SET @Plan=JSON_MODIFY(@Plan,'$.proposedJournals',JSON_QUERY(@Journals));
  SET @ProjectedControls=(SELECT DISTINCT @Bill itemId,mapping.AccountId accountId,mapping.AccountVersion accountVersion
    FROM @Resolved invoice CROSS APPLY OPENJSON(invoice.Mappings) WITH(Slot varchar(40) '$.slot',AccountId uniqueidentifier '$.accountId',
      AccountVersion uniqueidentifier '$.accountVersion') mapping WHERE invoice.Side='Invoice' AND mapping.Slot='SupplierPayable'
    FOR JSON PATH);
  DECLARE @FutureBindings nvarchar(max)=(SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(source.value,'$.replacementId')) creditId,
    invoice.EventId recognitionEventId,JSON_VALUE(claim.value,'$.componentKey') componentKey,
    JSON_VALUE(claim.value,'$.amount') amount,mapping.AccountId accountId,mapping.AccountVersion accountVersion
    FROM OPENJSON(@Plan,'$.sources') source CROSS APPLY OPENJSON(source.value,'$.replacement.claims') claim
    JOIN @Resolved invoice ON invoice.EventId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(claim.value,'$.recognitionEventId')) AND invoice.Side='Invoice'
    LEFT JOIN @Resolved recognition ON recognition.UnitId=invoice.UnitId AND recognition.Side='Recognition'
    CROSS APPLY OPENJSON(COALESCE(recognition.Mappings,invoice.Mappings)) WITH(Slot varchar(40) '$.slot',AccountId uniqueidentifier '$.accountId',
      AccountVersion uniqueidentifier '$.accountVersion') mapping
    WHERE mapping.Slot=CASE WHEN recognition.EventId IS NULL THEN 'Prepayment' ELSE 'Expense' END
    ORDER BY creditId,invoice.EventId,componentKey FOR JSON PATH);
  IF @PositionBindings IS NULL OR @PositionBindings=N'[]'
    SET @PositionBindings=COALESCE(@FutureBindings,N'[]');
  ELSE IF @FutureBindings IS NOT NULL AND @FutureBindings<>N'[]'
    SET @PositionBindings=LEFT(@PositionBindings,LEN(@PositionBindings)-1)+N','+SUBSTRING(@FutureBindings,2,LEN(@FutureBindings));
END;
