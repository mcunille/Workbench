-- Copyright (c) 2026 The White Stag Collection.
  IF @SourceCommandKind=N'Supplier.RefundRelease'
  BEGIN
    DECLARE @ReleaseOwner uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,'$.ownerRequestId')),
      @ReleaseGroup uniqueidentifier,@ReleaseCorrection uniqueidentifier,@ReleaseNow datetimeoffset,@ReleasePlan nvarchar(max),
      @ReleaseDetails nvarchar(max)=(SELECT @PostingDate postingDate FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    EXEC Purchasing.RequireSupplierSourceComposition @ActorId,@SessionId,@ReleaseOwner,N'ReleaseSettlement',@SourceId,@ReleaseDetails,
      @ReleaseGroup OUTPUT,@ReleaseCorrection OUTPUT,@ReleaseNow OUTPUT,@ReleasePlan OUTPUT;
    IF @RequiredPermission<>N'SupplierCreditsCorrect' OR @SourceKind<>N'SupplierRefundRelease' OR @EventKind<>N'Release'
      OR @SourceRevision<>@ReleaseCorrection OR @ReleaseGroup<>TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,'$.groupId'))
      OR CONVERT(varbinary(max),@CanonicalInput)<>CONVERT(varbinary(max),@SourceSnapshot) OR @SupplierControlItems IS NOT NULL
      OR (SELECT COUNT(*) FROM @Input)<>2
      THROW 51004,'Authentic source-owned refund release required.',1;
    INSERT @Historical
      SELECT 1,control.AccountId,control.AccountVersion,control.AccountCode,control.AccountName,control.AccountType,control.AccountPurpose
      FROM Purchasing.SupplierCashSettlements settlement
      CROSS APPLY Purchasing.SupplierCashControl(@TenantId,settlement.CashSourceId) cashControl
      CROSS APPLY Purchasing.SupplierCreditControl(@TenantId,settlement.ItemId) control
      JOIN @Input input ON input.Ordinal=1 AND input.AccountId=control.AccountId AND input.AccountVersion=control.AccountVersion
        AND input.Debit=settlement.Amount AND input.Credit=0
      WHERE settlement.TenantId=@TenantId AND settlement.Id=@SourceId
        AND settlement.ItemId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,'$.fundingItemId'))
        AND settlement.CashSourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,'$.cashSourceId'))
        AND settlement.Amount=TRY_CONVERT(decimal(28,4),JSON_VALUE(@SourceSnapshot,'$.amount'));
    IF @@ROWCOUNT<>1 OR NOT EXISTS(SELECT 1 FROM @Input input JOIN Accounting.Accounts account
        ON account.TenantId=@TenantId AND account.Id=input.AccountId AND account.Version=input.AccountVersion
      JOIN Accounting.Configurations config ON config.TenantId=account.TenantId AND config.Version=@ExpectedConfigurationVersion
      CROSS APPLY OPENJSON(config.Payload,'$.mappings') mapping
      WHERE input.Ordinal=2 AND input.Debit=0 AND input.Credit=TRY_CONVERT(decimal(28,4),JSON_VALUE(@SourceSnapshot,'$.amount'))
        AND account.Type='Liability' AND account.Purpose='SupplierRefundClearing' AND account.ArchivedAtUtc IS NULL
        AND JSON_VALUE(mapping.value,'$.slot')='SupplierRefundClearing' AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(mapping.value,'$.accountId'))=account.Id)
      THROW 51004,'Complete refund release controls required.',1;
  END;

  IF @SourceCommandKind=N'Supplier.RecognitionCreditAdjustment'
  BEGIN
    DECLARE @AdjustmentOwner uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,'$.ownerRequestId')),
      @AdjustmentGroup uniqueidentifier,@AdjustmentCorrection uniqueidentifier,@AdjustmentNow datetimeoffset,@AdjustmentPlan nvarchar(max),
      @AdjustmentDetails nvarchar(max)=JSON_QUERY(@SourceSnapshot,'$.adjustment'),
      @AdjustmentRecognition uniqueidentifier=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,'$.adjustment.recognitionEventId'));
    EXEC Purchasing.RequireSupplierSourceComposition @ActorId,@SessionId,@AdjustmentOwner,N'RecognitionAdjustment',@AdjustmentRecognition,@AdjustmentDetails,
      @AdjustmentGroup OUTPUT,@AdjustmentCorrection OUTPUT,@AdjustmentNow OUTPUT,@AdjustmentPlan OUTPUT;
    IF @RequiredPermission<>N'SupplierCreditsCorrect' OR @SourceKind<>N'SupplierRecognitionCreditAdjustment' OR @EventKind<>N'Adjust'
      OR @SourceRevision<>@AdjustmentCorrection OR @AdjustmentGroup<>TRY_CONVERT(uniqueidentifier,JSON_VALUE(@SourceSnapshot,'$.groupId'))
      OR CONVERT(varbinary(max),@CanonicalInput)<>CONVERT(varbinary(max),@SourceSnapshot) OR @SupplierControlItems IS NOT NULL
      OR (SELECT COUNT(*) FROM @Input)<>2
      THROW 51004,'Authentic monetary recognition adjustment required.',1;
    INSERT @Historical
      SELECT input.Ordinal,original.AccountId,original.AccountVersion,original.AccountCode,original.AccountName,original.AccountType,original.AccountPurpose
      FROM @Input input
      JOIN Accounting.JournalLines original ON original.TenantId=@TenantId
        AND original.JournalId=CASE input.Ordinal WHEN 1 THEN TRY_CONVERT(uniqueidentifier,JSON_VALUE(@AdjustmentDetails,'$.expenseJournalId'))
          ELSE TRY_CONVERT(uniqueidentifier,JSON_VALUE(@AdjustmentDetails,'$.prepaymentJournalId')) END
        AND original.Ordinal=CASE input.Ordinal WHEN 1 THEN TRY_CONVERT(int,JSON_VALUE(@AdjustmentDetails,'$.expenseOrdinal'))
          ELSE TRY_CONVERT(int,JSON_VALUE(@AdjustmentDetails,'$.prepaymentOrdinal')) END
        AND original.AccountId=input.AccountId AND original.AccountVersion=input.AccountVersion
      WHERE original.Debit>0 AND original.Credit=0 AND original.AccountPurpose='General'
        AND (input.Ordinal=1 AND original.AccountType='Expense' OR input.Ordinal=2 AND original.AccountType='Asset')
        AND input.Debit=CASE WHEN input.Ordinal=1 AND TRY_CONVERT(decimal(28,4),JSON_VALUE(@AdjustmentDetails,'$.amount'))>0
            OR input.Ordinal=2 AND TRY_CONVERT(decimal(28,4),JSON_VALUE(@AdjustmentDetails,'$.amount'))<0
          THEN ABS(TRY_CONVERT(decimal(28,4),JSON_VALUE(@AdjustmentDetails,'$.amount'))) ELSE 0 END
        AND input.Credit=CASE WHEN input.Ordinal=2 AND TRY_CONVERT(decimal(28,4),JSON_VALUE(@AdjustmentDetails,'$.amount'))>0
            OR input.Ordinal=1 AND TRY_CONVERT(decimal(28,4),JSON_VALUE(@AdjustmentDetails,'$.amount'))<0
          THEN ABS(TRY_CONVERT(decimal(28,4),JSON_VALUE(@AdjustmentDetails,'$.amount'))) ELSE 0 END;
    IF NOT EXISTS(SELECT 1 FROM @Historical WHERE Ordinal=1)
      INSERT @Historical SELECT 1,expense.AccountId,expense.AccountVersion,expense.AccountCode,expense.AccountName,expense.AccountType,expense.AccountPurpose
        FROM Purchasing.SupplierRecognitionExpenseControl(@TenantId,@AdjustmentRecognition) expense
        JOIN @Input input ON input.Ordinal=1 AND input.AccountId=expense.AccountId AND input.AccountVersion=expense.AccountVersion
        WHERE expense.JournalId IS NULL
          AND input.Debit=CASE WHEN TRY_CONVERT(decimal(28,4),JSON_VALUE(@AdjustmentDetails,'$.amount'))>0
            THEN TRY_CONVERT(decimal(28,4),JSON_VALUE(@AdjustmentDetails,'$.amount')) ELSE 0 END
          AND input.Credit=CASE WHEN TRY_CONVERT(decimal(28,4),JSON_VALUE(@AdjustmentDetails,'$.amount'))<0
            THEN -TRY_CONVERT(decimal(28,4),JSON_VALUE(@AdjustmentDetails,'$.amount')) ELSE 0 END;
    IF (SELECT COUNT(*) FROM @Historical)<>2 THROW 51004,'Original monetary recognition snapshots required.',1;
  END;
