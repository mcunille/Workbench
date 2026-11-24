-- Copyright (c) 2026 The White Stag Collection.
ALTER PROCEDURE Accounting.CorrectJournal
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,
  @RequiredPermission nvarchar(max),@SourceCommandKind nvarchar(max),@SourceCommandVersion int,
  @CanonicalInput nvarchar(max),@OriginalJournalId uniqueidentifier,
  @ExpectedConfigurationVersion uniqueidentifier,@PostingDate date,
  @Reason nvarchar(max),@Evidence nvarchar(max),
  @ReplacementSourceRevision uniqueidentifier=NULL,@ReplacementDocumentDate date=NULL,
  @ReplacementRuleVersion int=NULL,@ReplacementSnapshot nvarchar(max)=NULL,@ReplacementLines nvarchar(max)=NULL
AS BEGIN
  SET NOCOUNT ON; SET XACT_ABORT ON;
  IF @@TRANCOUNT=0 THROW 51000,'An outer source transaction is required.',1;
  DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),
    @LockResult int,@Resource nvarchar(255);
  IF @TenantId IS NULL OR @ActorId IS NULL OR @SessionId IS NULL
    THROW 51003,'Current tenant and actor authority are required.',1;
  IF @RequiredPermission IS NULL OR DATALENGTH(@RequiredPermission) NOT BETWEEN 2 AND 200
    THROW 51000,'A bounded source permission is required.',1;
  SET @Resource=N'Accounting:'+CONVERT(nvarchar(36),@TenantId);
  EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
  IF @LockResult<0 THROW 51009,'Accounting is being changed. Retry the request.',1;
  BEGIN TRY EXEC Accounting.RequirePermission @ActorId,@SessionId,@RequiredPermission; END TRY
  BEGIN CATCH IF ERROR_NUMBER()=50903 THROW 51003,'Current source authority is required.',1; THROW; END CATCH;
  IF EXISTS(SELECT 1 FROM Accounting.JournalEntries j JOIN Accounting.SourceEvents s
      ON s.TenantId=j.TenantId AND s.Id=j.SourceEventId WHERE j.TenantId=@TenantId AND j.Id=@OriginalJournalId
      AND (s.SourceKind IN('SupplierPayment','SupplierApplication','SupplierApplicationReversal')
        OR EXISTS(SELECT 1 FROM Purchasing.SupplierItemMovements m WHERE m.TenantId=s.TenantId AND m.SourceEventId=s.Id
          AND m.RecognitionEventId IS NULL)
        OR EXISTS(SELECT 1 FROM Purchasing.SupplierSourceCorrectionMembers member WHERE member.TenantId=j.TenantId AND member.JournalId=j.Id)))
    THROW 51009,'Supplier journals require their complete source-owner correction.',1;
  EXEC Accounting.CorrectJournalCore @ActorId,@SessionId,@RequestId,@RequiredPermission,@SourceCommandKind,@SourceCommandVersion,
    @CanonicalInput,@OriginalJournalId,@ExpectedConfigurationVersion,@PostingDate,@Reason,@Evidence,
    @ReplacementSourceRevision,@ReplacementDocumentDate,@ReplacementRuleVersion,@ReplacementSnapshot,@ReplacementLines;
END;
