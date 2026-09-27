// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class PurchaseRecognitionPurchaseGuard
{
    internal static void Up(MigrationBuilder migrationBuilder)
    {
        // The merged beta integration dropped V2/V3/V4 writers. Patch the installed current
        // commands in this forward migration, preserving their signatures, replay and state guards.
        foreach (var name in new[] { "SavePurchaseOrder", "SaveDraftOrder", "DeleteDraftOrder" })
            migrationBuilder.Sql($"""
                DECLARE @Command nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.{name}'));
                IF @Command IS NULL OR CHARINDEX(N'BEGIN TRANSACTION;',@Command)=0
                  THROW 50020,'Unsupported purchase recognition locking predecessor.',1;
                SET @Command=REPLACE(@Command,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
                SET @Command=REPLACE(@Command,N'BEGIN TRANSACTION;',N'BEGIN TRANSACTION;
                  DECLARE @AccountingLock int,@AccountingResource nvarchar(255)=N''Accounting:''+CONVERT(nvarchar(36),@TenantId);
                  EXEC @AccountingLock=sys.sp_getapplock @Resource=@AccountingResource,@LockMode=''Exclusive'',@LockOwner=''Transaction'',@LockTimeout=15000;
                  IF @AccountingLock<0 THROW 50409,''Accounting is being changed. Retry.'',1;');
                EXEC sys.sp_executesql @Command;
                """);

        migrationBuilder.Sql("""
            DECLARE @Command nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.SavePurchaseOrder'));
            DECLARE @Anchor nvarchar(max)=N'EXEC Purchasing.ValidatePurchaseOrderContent @Draft,@TenantId,@TargetId,@Calculation OUTPUT;';
            IF @Command IS NULL OR CHARINDEX(@Anchor,@Command)=0
              THROW 50020,'Unsupported purchase recognition identity predecessor.',1;
            SET @Command=REPLACE(@Command,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Command=REPLACE(@Command,@Anchor,@Anchor+N'
              IF @Operation=''Amend'' AND EXISTS(SELECT 1 FROM Purchasing.RecognitionUnits
                WHERE TenantId=@TenantId AND PurchaseOrderId=@TargetId
                  AND (TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Draft,''$.supplierId'')) IS NULL
                    OR SupplierId<>TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Draft,''$.supplierId''))))
                THROW 50415,''Supplier is fixed after financial evidence.'',1;');
            EXEC sys.sp_executesql @Command;
            """);
    }
}
