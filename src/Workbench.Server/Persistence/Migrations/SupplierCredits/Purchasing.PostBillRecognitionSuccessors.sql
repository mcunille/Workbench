-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.PostBillRecognitionSuccessors
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@OwnerRequestId uniqueidentifier,@OriginalBillId uniqueidentifier
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Tenant uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId')),@Group uniqueidentifier,@Correction uniqueidentifier,
    @Now datetimeoffset,@Plan nvarchar(max),@Post nvarchar(max),@Command nvarchar(max),@Result nvarchar(max),@Request uniqueidentifier,
    @Original uniqueidentifier,@Unit uniqueidentifier,@Event uniqueidentifier,@Journal uniqueidentifier,@PostingPermission nvarchar(100);
  SELECT @Post=JSON_QUERY(ResultJson,'$.plan.sources[0].replacement.post') FROM Purchasing.SupplierFinancialReceipts
    WHERE TenantId=@Tenant AND RequestId=@OwnerRequestId;
  EXEC Purchasing.RequireSupplierBillComposition @ActorId,@SessionId,@OwnerRequestId,N'RecognitionSuccessors',@OriginalBillId,@Post,
    @Group OUTPUT,@Correction OUTPUT,@Now OUTPUT,@Plan OUTPUT;
  DECLARE successors CURSOR LOCAL FAST_FORWARD FOR SELECT TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.originalEventId')),
    TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.unitId')),TRY_CONVERT(uniqueidentifier,JSON_VALUE(value,'$.eventId')),JSON_QUERY(value,'$.command')
    FROM OPENJSON(@Plan,'$.recognitionSuccessors') ORDER BY JSON_VALUE(value,'$.originalEventId');
  OPEN successors; FETCH NEXT FROM successors INTO @Original,@Unit,@Event,@Command;
  WHILE @@FETCH_STATUS=0
  BEGIN
    SET @Request=NEWID();
    SELECT @PostingPermission=registered.PostingPermission FROM Purchasing.RecognitionSideEvents original
      JOIN Purchasing.RecognitionSourceOwners registered ON registered.PostingPermission=original.EvidenceMutationPermission COLLATE Latin1_General_100_BIN2
      WHERE original.TenantId=@Tenant AND original.Id=@Original;
    IF @PostingPermission IS NULL THROW 51004,'The authentic upstream posting owner is unavailable.',1;
    EXEC Purchasing.PostRecognition @ActorId=@ActorId,@SessionId=@SessionId,@RequestId=@Request,
      @RequiredPermission=@PostingPermission,@Command=@Command,@ResultJson=@Result OUTPUT,@SuppressResult=1,@BillOwnerRequestId=@OwnerRequestId;
    INSERT Purchasing.RecognitionGroupReceipts(TenantId,RequestId,ActorId,CommandKind,CommandVersion,CanonicalInput,InputSha256,ResultJson,RecordedAtUtc)
      VALUES(@Tenant,@Request,@ActorId,'Post',1,@Command,HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Command)),@Result,@Now);
    SELECT @Journal=JournalId FROM Purchasing.RecognitionSideEvents WHERE TenantId=@Tenant AND Id=@Event AND UnitId=@Unit;
    UPDATE Purchasing.RecognitionEventCorrections SET ReplacementEventId=@Event WHERE TenantId=@Tenant AND OriginalEventId=@Original;
    INSERT Purchasing.SupplierSourceCorrectionMembers(TenantId,Id,CorrectionId,GroupId,Kind,EntityId,JournalId,RecognitionEventId,EffectsJson)
      VALUES(@Tenant,NEWID(),@Correction,@Group,'Replacement',@Event,@Journal,@Event,@Result);
    FETCH NEXT FROM successors INTO @Original,@Unit,@Event,@Command;
  END;
  CLOSE successors; DEALLOCATE successors;
  UPDATE correction SET ReplacementUnitId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(unit.value,'$.binding.successorUnitId')),
    ReplacementMatchId=(SELECT matched.Id FROM Purchasing.RecognitionMatches matched WHERE matched.TenantId=@Tenant
      AND matched.UnitId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(unit.value,'$.binding.successorUnitId')))
    FROM Purchasing.RecognitionCorrectionGroups correction CROSS APPLY OPENJSON(@Plan,'$.units') unit
    WHERE correction.TenantId=@Tenant AND correction.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(unit.value,'$.binding.correctionGroupId'))
      AND correction.RecordedAtUtc=@Now AND correction.ActorId=@ActorId;
END;
