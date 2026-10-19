-- Copyright (c) 2026 The White Stag Collection.
CREATE PROCEDURE Purchasing.RecordSupplierRefund
  @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
AS BEGIN
  SET NOCOUNT ON;
  DECLARE @Result nvarchar(max);
  EXEC Purchasing.RecordSupplierCashCore @ActorId,@SessionId,@RequestId,@Command,'SupplierRefund',@Result OUTPUT;
END;
