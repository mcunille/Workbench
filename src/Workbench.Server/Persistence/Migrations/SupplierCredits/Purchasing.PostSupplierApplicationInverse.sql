-- Copyright (c) 2026 The White Stag Collection.
ALTER PROCEDURE Purchasing.PostSupplierApplicationInverse
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@ApplicationId uniqueidentifier,@GroupId uniqueidentifier,
  @RecordedAtUtc datetimeoffset,@ConfigurationVersion uniqueidentifier,@PostingDate date,@Reason nvarchar(max),
  @ReversalId uniqueidentifier OUTPUT,@JournalId uniqueidentifier OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),
    @Lines nvarchar(max),@Snapshot nvarchar(max),@References nvarchar(max),@Request uniqueidentifier=NEWID();
  IF @@TRANCOUNT=0 OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Supplier inverse requires its owner transaction.',1;
  EXEC Accounting.RequirePermission @ActorId,@SessionId,N'SupplierAllocationsManage';
  IF EXISTS(SELECT 1 FROM Purchasing.SupplierApplicationControl(@TenantId,@ApplicationId) a
      JOIN Accounting.CorrectionGroups c ON c.TenantId=@TenantId AND c.OriginalJournalId=a.JournalId)
    THROW 51009,'Supplier application has an unsupported external correction.',1;
  SET @ReversalId=NEWID();
  SET @Snapshot=(SELECT @ApplicationId applicationId,@GroupId groupId,CONVERT(nvarchar(60),a.Amount) amount
    FROM Purchasing.SupplierApplicationControl(@TenantId,@ApplicationId) a FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
  SET @Lines=(SELECT line.Ordinal ordinal,line.AccountId accountId,line.AccountVersion accountVersion,
    CONVERT(nvarchar(60),line.Debit) debit,CONVERT(nvarchar(60),line.Credit) credit
    FROM Purchasing.SupplierApplicationControl(@TenantId,@ApplicationId) a
    CROSS APPLY Accounting.ControlJournalPair(a.DebtAccountId,a.DebtAccountVersion,a.FundingAccountId,a.FundingAccountVersion,-a.Amount) line
    ORDER BY line.Ordinal FOR JSON PATH);
  SET @References=N'[]';
  DECLARE @Posted TABLE(SourceEventId uniqueidentifier,JournalId uniqueidentifier,Sequence bigint,RecordedAtUtc datetimeoffset);
  DECLARE @Currency varchar(3)=(SELECT i.Currency FROM Purchasing.SupplierApplications a
    JOIN Purchasing.SupplierOpenItems i ON i.TenantId=a.TenantId AND i.Id=a.FundingItemId WHERE a.TenantId=@TenantId AND a.Id=@ApplicationId);
  INSERT @Posted EXEC Accounting.PostJournal @ActorId,@SessionId,@Request,N'SupplierAllocationsManage',N'Supplier.ReverseApplication',1,
    @Snapshot,N'SupplierApplicationReversal',@ReversalId,@GroupId,N'Reverse',1,@ConfigurationVersion,@Currency,
    @PostingDate,@PostingDate,@PostingDate,NULL,@Reason,@Snapshot,@Lines,@References;
  SELECT @JournalId=JournalId FROM @Posted;
  UPDATE s SET RecordedAtUtc=@RecordedAtUtc FROM Accounting.SourceEvents s JOIN @Posted p ON p.SourceEventId=s.Id WHERE s.TenantId=@TenantId;
  UPDATE j SET RecordedAtUtc=@RecordedAtUtc FROM Accounting.JournalEntries j JOIN @Posted p ON p.JournalId=j.Id WHERE j.TenantId=@TenantId;
  UPDATE Accounting.PostingReceipts SET RecordedAtUtc=@RecordedAtUtc WHERE TenantId=@TenantId AND RequestId=@Request;
  UPDATE Security.TenantSecurityAuditEvents SET OccurredAtUtc=@RecordedAtUtc
    WHERE TenantId=@TenantId AND Action='Accounting.PostJournal' AND TargetId=@JournalId;
END;
