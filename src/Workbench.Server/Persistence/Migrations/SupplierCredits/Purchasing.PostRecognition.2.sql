-- Copyright (c) 2026 The White Stag Collection.
    DECLARE @MinorUnit decimal(28,4)=CASE @Scale WHEN 0 THEN 1 WHEN 1 THEN 0.1 WHEN 2 THEN 0.01 WHEN 3 THEN 0.001 ELSE 0.0001 END;
    IF EXISTS(SELECT 1 FROM @Components WHERE Kind='Rounding' AND (ABS(Amount)>@MinorUnit OR @Side<>'Invoice'))
      OR (EXISTS(SELECT 1 FROM @Components WHERE Kind='Rounding') AND EXISTS(
        SELECT 1 FROM Purchasing.ActiveRecognitionSideEvents e WITH(UPDLOCK,HOLDLOCK)
        JOIN Purchasing.RecognitionComponents c ON c.TenantId=e.TenantId AND c.EventId=e.Id
        WHERE e.TenantId=@TenantId AND e.Side='Invoice' AND e.SourceId=@SourceId AND c.Kind='Rounding'))
      THROW 51000,'Invoice source already has rounding or its bound is exceeded.',1;
    DECLARE @CostWide decimal(38,4),@TaxWide decimal(38,4),@Cost decimal(28,4),@Tax decimal(28,4);
    SELECT @CostWide=Cost,@TaxWide=Tax FROM Purchasing.RecognitionComponentTotals(JSON_QUERY(@SideInput,'$.components'));
    IF @CostWide<0 OR @CostWide>999999999999999999999999.9999 OR @TaxWide>999999999999999999999999.9999
      OR (@Side='Recognition' AND @TaxWide<>0) THROW 51000,'Component totals do not match the source.',1;
    IF EXISTS(SELECT 1 FROM @Components b WHERE b.Kind IN ('BaseCost','Freight','Charge') AND
        b.Amount+COALESCE((SELECT SUM(CASE WHEN c.Kind='Discount' THEN -CONVERT(decimal(38,4),c.Amount)
          WHEN c.Kind IN ('NonrecoverableTax','Rounding') THEN CONVERT(decimal(38,4),c.Amount) ELSE 0 END)
          FROM @Components c WHERE c.AssignedCostComponentKey COLLATE Latin1_General_100_BIN2=b.ComponentKey),0)<0)
      THROW 51000,'Assigned cost component cannot become negative.',1;
    SET @Cost=CONVERT(decimal(28,4),@CostWide); SET @Tax=CONVERT(decimal(28,4),@TaxWide);
    IF @Cost+@Tax<>@SourceAmount OR @Cost+@Tax>999999999999999999999999.9999
      THROW 51000,'Component totals do not match the source.',1;
    IF EXISTS(SELECT 1 FROM @Components WHERE Kind IN ('RecoverableTax','NonrecoverableTax') AND Amount>0)
      AND JSON_VALUE(@Evidence,'$.taxPolicyReference') IS NULL
      THROW 51000,'Tax requires reviewed policy evidence.',1;
    IF @Tax>0 AND COALESCE(JSON_VALUE(@Evidence,'$.taxEntitlement'),'false')<>'true'
      THROW 51000,'Recoverable tax requires reviewed policy and current entitlement.',1;
    DECLARE @Variance decimal(28,4)=@Cost-@PriorCost;
    IF @Side='Invoice' AND @PriorEventId IS NOT NULL AND @Variance<>0
    BEGIN
      IF JSON_VALUE(@Evidence,'$.varianceAmount') IS NULL OR CONVERT(decimal(28,4),JSON_VALUE(@Evidence,'$.varianceAmount'))<>@Variance
        OR JSON_VALUE(@Evidence,'$.varianceReason') IS NULL OR COALESCE(JSON_VALUE(@Evidence,'$.varianceClassification'),'') COLLATE Latin1_General_100_BIN2<>@Classification
        THROW 51000,'Cost difference requires exact reviewed variance evidence.',1;
      -- The ungranted entrypoint consumes source-derived state; the trusted adapter must bind
      -- this evidence to durable inventory, under this transaction's source/accounting lock.
      IF @Classification='Inventory' AND COALESCE(JSON_VALUE(@Evidence,'$.inventoryAdjustmentState'),'') COLLATE Latin1_General_100_BIN2<>'Held'
        THROW 51000,'Inventory state cannot receive this cost adjustment.',1;
    END;
    DECLARE @Mappings nvarchar(max),@LinesJson nvarchar(max),@PriorMappings nvarchar(max)=JSON_QUERY(@PriorEvidence,'$.accountMappings'),
      @HasPrior bit=CASE WHEN @PriorEventId IS NULL THEN 0 ELSE 1 END;
    EXEC Purchasing.BuildSupplierRecognitionJournalLines @TenantId,@Config,@Side,@Classification,@Cost,@Tax,@HasPrior,@PriorCost,
      @PriorMappings,@Mappings OUTPUT,@LinesJson OUTPUT;
    -- Keep SQL-owned mappings even for a zero-only event.
    SET @Evidence=JSON_MODIFY(@Evidence,'$.accountMappings',JSON_QUERY(@Mappings));
    IF DATALENGTH(@Evidence)>262144 THROW 51000,'Recognition evidence exceeds its bound.',1;
    DECLARE @EventId uniqueidentifier=NEWID(),@JournalId uniqueidentifier=NULL,@JournalRequestId uniqueidentifier=NEWID(),@Snapshot nvarchar(max);
    IF @BillOwnerRequestId IS NOT NULL
    BEGIN
      IF @Side='Invoice' SET @EventId=CONVERT(uniqueidentifier,JSON_VALUE(@BillBinding,'$.invoiceEventId'));
      ELSE SELECT @EventId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.eventId')) FROM OPENJSON(@BillPlan,'$.recognitionSuccessors')
        WHERE TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.unitId'))=@UnitId;
    END;
    IF @LinesJson<>N'[]'
    BEGIN
      SET @Snapshot=(SELECT @UnitId unitId,@EventId eventId,@PurchaseOrderId purchaseOrderId,@PoRevision purchaseOrderRevision,@SupplierId supplierId,
        @Classification classification,JSON_QUERY(@CreditDependencies) supplierCreditDependencies,JSON_QUERY(@SideInput) side FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
      DECLARE @Posted TABLE(SourceEventId uniqueidentifier,JournalId uniqueidentifier,Sequence bigint,RecordedAtUtc datetimeoffset);
      DELETE @Posted;
      INSERT @Posted EXEC Accounting.PostJournal @ActorId=@ActorId,@SessionId=@SessionId,@RequestId=@JournalRequestId,
        @RequiredPermission=@RequiredPermission,@SourceCommandKind=N'PurchaseRecognition',@SourceCommandVersion=1,@CanonicalInput=@Canonical,
        @SourceKind=N'PurchaseRecognition',@SourceId=@EventId,@SourceRevision=@SourceRevision,@EventKind=@Side,@RuleVersion=1,
        @ExpectedConfigurationVersion=@ConfigurationVersion,@Currency=@Currency,@DocumentDate=@DocumentDate,@EffectiveDate=@EffectiveDate,
        @PostingDate=@PostingDate,@SourceSnapshot=@Snapshot,@Lines=@LinesJson;
      SELECT @JournalId=JournalId FROM @Posted;
      -- Normalize only rows created by this transaction's kernel call to the group's recorded instant.
      UPDATE Accounting.SourceEvents SET RecordedAtUtc=@Now WHERE TenantId=@TenantId AND Id IN (SELECT SourceEventId FROM @Posted);
      UPDATE Accounting.JournalEntries SET RecordedAtUtc=@Now WHERE TenantId=@TenantId AND Id=@JournalId;
      UPDATE Accounting.PostingReceipts SET RecordedAtUtc=@Now WHERE TenantId=@TenantId AND RequestId=@JournalRequestId;
      UPDATE Accounting.PolicyFreezes SET RecordedAtUtc=@Now WHERE TenantId=@TenantId AND FirstJournalId=@JournalId;
      UPDATE Security.TenantSecurityAuditEvents SET OccurredAtUtc=@Now WHERE TenantId=@TenantId AND TargetId=@JournalId AND Action='Accounting.PostJournal';
    END;
    IF NOT EXISTS(SELECT 1 FROM Purchasing.RecognitionUnits WHERE TenantId=@TenantId AND Id=@UnitId)
    INSERT Purchasing.RecognitionUnits(TenantId,Id,PurchaseOrderId,PurchaseOrderRevision,SupplierId,Currency,GoodsReference,Classification,Quantity,QuantityUnit,PolicyVersion,CreatedAtUtc)
      VALUES(@TenantId,@UnitId,@PurchaseOrderId,@PoRevision,@SupplierId,@Currency,JSON_VALUE(@Unit,'$.goodsReference'),@Classification,@Quantity,JSON_VALUE(@Unit,'$.quantityUnit'),1,@Now);
    INSERT Purchasing.RecognitionSideEvents(TenantId,Id,UnitId,Side,EventRevision,SourceId,SourceRevision,SourceComponentKey,SubdivisionKey,SourceQuantity,SourceAmount,
      DocumentDate,EffectiveDate,PostingDate,ConfigurationVersion,ActorId,EvidenceMutationPermission,EvidenceJson,EvidenceSha256,JournalId,CorrectionGroupId,RecordedAtUtc)
      VALUES(@TenantId,@EventId,@UnitId,@Side,@EventRevision,@SourceId,@SourceRevision,@SourceComponentKey,@SubdivisionKey,@SourceQuantity,@SourceAmount,
        @DocumentDate,@EffectiveDate,@PostingDate,@ConfigurationVersion,@ActorId,@RequiredPermission,@Evidence,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Evidence)),@JournalId,@ParentCorrectionGroupId,@Now);
    INSERT Purchasing.RecognitionComponents(TenantId,EventId,ComponentKey,Kind,Amount,Reason,AssignedCostComponentKey)
      SELECT @TenantId,@EventId,ComponentKey,Kind,Amount,Reason,AssignedCostComponentKey FROM @Components;
    IF NOT EXISTS(SELECT 1 FROM Purchasing.SupplierBillRevisions WHERE TenantId=@TenantId AND BillId=@SourceId AND Id=@SourceRevision)
BEGIN
  DECLARE @FinancialEvidenceSet uniqueidentifier;
  EXEC Accounting.CaptureFinancialEvidence 'PurchaseRecognition',@EventId,@SourceRevision,@FinancialEvidenceSet OUTPUT;
END;
INSERT @Events VALUES(@EventId,@UnitId,@JournalId);
    IF @PriorEventId IS NOT NULL
    BEGIN
      DECLARE @MatchId uniqueidentifier=NEWID();
      INSERT Purchasing.RecognitionMatches(TenantId,Id,UnitId,RecognitionEventId,InvoiceEventId,CorrectionGroupId,RecordedAtUtc)
        VALUES(@TenantId,@MatchId,@UnitId,CASE WHEN @Side='Recognition' THEN @EventId ELSE @PriorEventId END,
          CASE WHEN @Side='Invoice' THEN @EventId ELSE @PriorEventId END,@ParentCorrectionGroupId,@Now);
      INSERT @Matches VALUES(@MatchId);
    END;
    SET @SideOrdinal+=1;
    END;
    FETCH NEXT FROM unit_cursor INTO @UnitId,@Unit;
  END;
  CLOSE unit_cursor; DEALLOCATE unit_cursor;
  DECLARE @UnitIds nvarchar(max)=(SELECT N'['+STRING_AGG(CONVERT(nvarchar(max),N'"'+CONVERT(nvarchar(36),Id)+N'"'),N',') WITHIN GROUP(ORDER BY Id)+N']' FROM @Units),
    @EventIds nvarchar(max)=(SELECT N'['+STRING_AGG(CONVERT(nvarchar(max),N'"'+CONVERT(nvarchar(36),Id)+N'"'),N',') WITHIN GROUP(ORDER BY Id)+N']' FROM @Events),
    @MatchIds nvarchar(max)=(SELECT N'['+COALESCE(STRING_AGG(CONVERT(nvarchar(max),N'"'+CONVERT(nvarchar(36),Id)+N'"'),N','),N'')+N']' FROM @Matches),
    @JournalIds nvarchar(max)=(SELECT N'['+COALESCE(STRING_AGG(CONVERT(nvarchar(max),N'"'+CONVERT(nvarchar(36),JournalId)+N'"'),N',') WITHIN GROUP(ORDER BY JournalId),N'')+N']' FROM @Events WHERE JournalId IS NOT NULL),
    @Result nvarchar(max);
  SET @Result=(SELECT @CommandId commandId,JSON_QUERY(@UnitIds) unitIds,JSON_QUERY(@EventIds) eventIds,JSON_QUERY(@MatchIds) matchIds,
    CONVERT(uniqueidentifier,NULL) correctionGroupId,JSON_QUERY(@JournalIds) journalIds,@Now recordedAtUtc FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES);
  SET @ResultJson=@Result;
  IF @ParentCorrectionGroupId IS NOT NULL RETURN;
  INSERT Purchasing.RecognitionGroupReceipts(TenantId,RequestId,ActorId,CommandKind,CommandVersion,CanonicalInput,InputSha256,ResultJson,RecordedAtUtc)
    VALUES(@TenantId,@RequestId,@ActorId,'Post',1,@Canonical,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Canonical)),@Result,@Now);

  IF NOT EXISTS(SELECT 1 FROM @Events posted JOIN Purchasing.RecognitionSideEvents e ON e.TenantId=@TenantId AND e.Id=posted.Id
    JOIN Purchasing.SupplierBillRevisions b ON b.TenantId=e.TenantId AND b.BillId=e.SourceId AND b.Id=e.SourceRevision)
    EXEC Purchasing.DeriveRecognitionOpenItems @TenantId,@RequestId;
  INSERT Security.TenantSecurityAuditEvents(Id,TenantId,ActorUserId,Action,TargetType,TargetId,OccurredAtUtc)
    VALUES(NEWID(),@TenantId,@ActorId,N'Purchasing.PostRecognition',N'RecognitionCommand',@CommandId,@Now);
  IF @SuppressResult=0 SELECT @Result ResultJson;
END;
