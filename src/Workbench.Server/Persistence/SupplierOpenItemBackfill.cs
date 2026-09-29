// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.Persistence;

internal static class SupplierOpenItemBackfill
{
    // Runs inside the forward migration transaction. No source, journal or receipt bytes are changed.
    internal const string Sql = """
        DECLARE @Tenant uniqueidentifier,@Request uniqueidentifier,@Bill uniqueidentifier,@Lock int,@Resource nvarchar(255);
        DECLARE supplier_history CURSOR LOCAL FAST_FORWARD FOR
          SELECT r.TenantId,r.RequestId,b.BillId FROM Purchasing.RecognitionGroupReceipts r
          LEFT JOIN Purchasing.SupplierBillPostings b ON b.TenantId=r.TenantId AND b.RecognitionRequestId=r.RequestId
          ORDER BY r.TenantId,r.RecordedAtUtc,CASE WHEN r.CommandKind='Post' THEN 0 ELSE 1 END,r.RequestId;
        OPEN supplier_history; FETCH NEXT FROM supplier_history INTO @Tenant,@Request,@Bill;
        WHILE @@FETCH_STATUS=0
        BEGIN
          SET @Resource=N'Accounting:'+CONVERT(nvarchar(36),@Tenant);
          EXEC @Lock=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
          IF @Lock<0 THROW 50020,'Supplier history is being changed. Retry the migration.',1;
          EXEC Purchasing.DeriveRecognitionOpenItems @Tenant,@Request,@Bill;
          FETCH NEXT FROM supplier_history INTO @Tenant,@Request,@Bill;
        END;
        CLOSE supplier_history; DEALLOCATE supplier_history;
        """;
}
