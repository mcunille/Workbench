-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.PostSupplierBillSuccessor
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@OwnerRequestId uniqueidentifier,@BillId uniqueidentifier,@Replacement nvarchar(max),
  @ResultJson nvarchar(max) OUTPUT
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Post nvarchar(max)=JSON_QUERY(@Replacement,'$.post'),@Group uniqueidentifier,@Correction uniqueidentifier,@Now datetimeoffset,@Plan nvarchar(max),@Request uniqueidentifier=NEWID();
  EXEC Purchasing.CanonicalizeSupplierBill @Post,@Post OUTPUT;
  EXEC Purchasing.RequireSupplierBillComposition @ActorId,@SessionId,@OwnerRequestId,N'ReplacementBill',@BillId,@Post,
    @Group OUTPUT,@Correction OUTPUT,@Now OUTPUT,@Plan OUTPUT;
  EXEC Purchasing.PostSupplierBillCore @ActorId,@SessionId,@Request,@Post,@OwnerRequestId,@ResultJson OUTPUT,1;
  DECLARE @OriginalBill uniqueidentifier=CONVERT(uniqueidentifier,JSON_VALUE(@Plan,'$.sources[0].sourceId'));
  EXEC Purchasing.PostBillRecognitionSuccessors @ActorId,@SessionId,@OwnerRequestId,@OriginalBill;
END;
