-- Copyright (c) 2026 The White Stag Collection.
ALTER PROCEDURE Purchasing.RequireSupplierComposition
  @ActorId uniqueidentifier,@OwnerRequestId uniqueidentifier,@RequestId uniqueidentifier,@Operation nvarchar(40),
  @Canonical nvarchar(max),@GroupId uniqueidentifier OUTPUT,@RecordedAtUtc datetimeoffset OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N'TenantId'));
  IF @@TRANCOUNT=0 OR @TenantId IS NULL OR APPLOCK_MODE('public',N'Accounting:'+CONVERT(nvarchar(36),@TenantId),'Transaction')<>'Exclusive'
    THROW 51009,'Supplier composition requires its owner transaction.',1;
  SELECT @GroupId=r.GroupId,@RecordedAtUtc=r.RecordedAtUtc
    FROM Purchasing.SupplierFinancialReceipts r JOIN Purchasing.SupplierFinancialGroups g ON g.TenantId=r.TenantId AND g.Id=r.GroupId
    CROSS APPLY OPENJSON(r.ResultJson,'$.compositions') WITH(RequestId uniqueidentifier '$.requestId',Operation nvarchar(40) '$.operation',Canonical nvarchar(max) '$.canonicalInput') c
    WHERE r.TenantId=@TenantId AND r.RequestId=@OwnerRequestId AND r.ActorId=@ActorId
      AND JSON_VALUE(r.ResultJson,'$.state')='pending' AND c.RequestId=@RequestId AND c.Operation=@Operation
      AND CONVERT(varbinary(max),c.Canonical)=CONVERT(varbinary(max),@Canonical) AND g.RecordedAtUtc=r.RecordedAtUtc
      AND ((@Operation='RecordSupplierPayment' AND r.Operation='CorrectSupplierPayment' AND g.Operation='CorrectPayment'
          AND g.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(r.CanonicalInput,'$.paymentId'))
          AND TRY_CONVERT(uniqueidentifier,JSON_VALUE(r.CanonicalInput,'$.replacement.paymentId'))=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.paymentId')))
        OR (@Operation='ApplySupplierFunds' AND r.Operation='ReverseSupplierApplication' AND g.Operation='ReverseApplication'
          AND g.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(r.CanonicalInput,'$.applicationId'))) 
        OR (@Operation='ApplySupplierFunds' AND r.Operation='PostSupplierCredit' AND g.Operation='RecordCredit'
          AND g.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(r.CanonicalInput,'$.creditId'))
          AND g.SourceId=TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Canonical,'$.fundingItemId'))));
  IF @GroupId IS NULL OR @RecordedAtUtc IS NULL THROW 51004,'Authentic pending supplier composition is unavailable.',1;
END;
