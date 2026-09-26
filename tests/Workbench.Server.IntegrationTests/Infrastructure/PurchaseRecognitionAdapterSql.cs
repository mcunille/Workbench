// Copyright (c) 2026 The White Stag Collection.
namespace Workbench.Server.IntegrationTests.Infrastructure;

internal static class PurchaseRecognitionAdapterSql
{
    internal const string Install = """
        INSERT [Identity].RoleClaims(TenantId,RoleId,ClaimType,ClaimValue)
          SELECT TenantId,RoleId,N'workbench/permission',N'PurchaseRecognitionFixturePost'
          FROM Administration.AccountingRoles WHERE Kind='Administrator';
        CREATE TABLE Purchasing.FixtureRecognitionSources(TenantId uniqueidentifier NOT NULL,Id uniqueidentifier NOT NULL,Revision uniqueidentifier NOT NULL,
          PurchaseOrderId uniqueidentifier NOT NULL,SupplierId uniqueidentifier NOT NULL,Currency varchar(3) NOT NULL,
          Classification varchar(16) NOT NULL,Side varchar(16) NOT NULL,SourceComponentKey nvarchar(200) NOT NULL DEFAULT N'line-1',EvidenceJson nvarchar(max) NOT NULL,
          PRIMARY KEY(TenantId,Id,Revision));
        EXEC(N'CREATE PROCEDURE Purchasing.PostFixtureRecognition
          @ActorId uniqueidentifier,@SessionId uniqueidentifier,@RequestId uniqueidentifier,@Command nvarchar(max)
        AS BEGIN
          SET NOCOUNT ON; SET XACT_ABORT ON;
          BEGIN TRY
            BEGIN TRANSACTION;
            DECLARE @TenantId uniqueidentifier=TRY_CONVERT(uniqueidentifier,SESSION_CONTEXT(N''TenantId'')),@LockResult int,
              @Resource nvarchar(255)=N''Accounting:''+CONVERT(nvarchar(36),SESSION_CONTEXT(N''TenantId''));
            EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode=''Exclusive'',@LockOwner=''Transaction'',@LockTimeout=10000;
            IF @LockResult<0 THROW 51009,''Accounting is being changed.'',1;
            BEGIN TRY
              EXEC Accounting.RequirePermission @ActorId,@SessionId,N''PurchaseRecognitionFixturePost'';
            END TRY BEGIN CATCH
              IF ERROR_NUMBER()=50903 THROW 51003,''Current source authority is required.'',1;
              THROW;
            END CATCH;
            -- Mutable source checks follow successful-receipt lookup. The kernel binds replay to actor and complete canonical input.
            IF NOT EXISTS(SELECT 1 FROM Purchasing.RecognitionGroupReceipts WHERE TenantId=@TenantId AND RequestId=@RequestId)
            BEGIN
              IF ISJSON(@Command,OBJECT)<>1 THROW 51000,''Invalid fixture envelope.'',1;
              IF EXISTS(SELECT 1 FROM OPENJSON(@Command,''$.units'') u CROSS APPLY OPENJSON(u.value,''$.sides'') s
                LEFT JOIN Purchasing.FixtureRecognitionSources f WITH(UPDLOCK,HOLDLOCK)
                  ON f.TenantId=@TenantId AND f.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(s.value,''$.sourceId''))
                  AND f.Revision=TRY_CONVERT(uniqueidentifier,JSON_VALUE(s.value,''$.sourceRevision''))
                WHERE f.Id IS NULL OR f.PurchaseOrderId<>TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Command,''$.purchaseOrderId''))
                  OR f.SupplierId<>TRY_CONVERT(uniqueidentifier,JSON_VALUE(@Command,''$.supplierId''))
                  OR f.Currency<>JSON_VALUE(@Command,''$.currency'') OR f.Classification<>JSON_VALUE(u.value,''$.classification'')
                  OR f.Side<>JSON_VALUE(s.value,''$.side'')
                  OR CONVERT(varbinary(max),f.SourceComponentKey)<>CONVERT(varbinary(max),JSON_VALUE(s.value,''$.sourceComponentKey''))
                  OR CONVERT(varbinary(max),f.EvidenceJson)<>CONVERT(varbinary(max),JSON_QUERY(s.value,''$.evidence'')))
                THROW 51004,''Source is unavailable or its durable evidence changed.'',1;
            END;
            EXEC Purchasing.PostRecognition @ActorId,@SessionId,@RequestId,N''PurchaseRecognitionFixturePost'',@Command;
            COMMIT;
          END TRY BEGIN CATCH
            IF @@TRANCOUNT>0 ROLLBACK;
            THROW;
          END CATCH;
        END');
        GRANT EXECUTE ON Purchasing.PostFixtureRecognition TO workbench_web;
        """;
}
