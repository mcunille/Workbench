// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class PurchaseRecognitionSchema
{
    internal static void Up(MigrationBuilder migrationBuilder, string migrationId)
    {
        migrationBuilder.Sql("""
            CREATE VIEW Purchasing.ActiveRecognitionSideEvents AS
              SELECT e.* FROM Purchasing.RecognitionSideEvents e WHERE NOT EXISTS
                (SELECT 1 FROM Purchasing.RecognitionEventCorrections c WHERE c.TenantId=e.TenantId AND c.OriginalEventId=e.Id);
            """);
        migrationBuilder.Sql(PurchaseRecognitionCommandValidation.Sql);
        migrationBuilder.Sql(PurchaseRecognitionPosting.Sql);
        migrationBuilder.Sql(PurchaseRecognitionCorrections.Sql);
        migrationBuilder.Sql("""
            DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Accounting.CorrectJournal'));
            IF @Definition IS NULL OR CHARINDEX(N'IF @OriginalEventId IS NULL',@Definition)=0
              THROW 50020,'Unsupported correction kernel predecessor.',1;
            SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Definition=REPLACE(@Definition,N'IF @OriginalEventId IS NULL',N'
              IF @SourceKind=N''PurchaseRecognition'' AND (@SourceCommandKind<>N''PurchaseRecognition.CorrectInternal'' OR NOT EXISTS(
                SELECT 1 FROM Purchasing.RecognitionEventCorrections c JOIN Purchasing.RecognitionSideEvents e ON e.TenantId=c.TenantId AND e.Id=c.OriginalEventId
                WHERE c.TenantId=@TenantId AND e.JournalId=@OriginalJournalId AND c.AccountingCorrectionGroupId IS NULL))
                THROW 51009,''Purchase recognition requires its complete dependency correction.'',1;
              IF @OriginalEventId IS NULL');
            EXEC sys.sp_executesql @Definition;
            """);
        migrationBuilder.Sql("""
            ALTER TABLE Purchasing.RecognitionUnits ADD CONSTRAINT FK_RecognitionUnits_OrderRevision
              FOREIGN KEY(TenantId,PurchaseOrderId,PurchaseOrderRevision)
              REFERENCES Purchasing.PurchaseOrderRevisions(TenantId,DraftOrderId,Revision);
            """);
        foreach (var table in new[] { "RecognitionUnits", "RecognitionSideEvents", "RecognitionComponents", "RecognitionMatches", "RecognitionCorrectionGroups", "RecognitionEventCorrections", "RecognitionGroupReceipts" })
            migrationBuilder.Sql($"""
                ALTER SECURITY POLICY Security.TenantIsolationPolicy
                    ADD FILTER PREDICATE Security.fn_tenant_access(TenantId) ON Purchasing.{table},
                    ADD BLOCK PREDICATE Security.fn_tenant_access(TenantId) ON Purchasing.{table} AFTER INSERT,
                    ADD BLOCK PREDICATE Security.fn_tenant_access(TenantId) ON Purchasing.{table} AFTER UPDATE;
                GRANT SELECT ON Purchasing.{table} TO workbench_web;
                DENY INSERT,UPDATE,DELETE ON Purchasing.{table} TO workbench_web;
                """);

        migrationBuilder.Sql("""
            DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Accounting.ValidateConfiguration'));
            IF @Definition IS NULL OR CHARINDEX(N'(SELECT COUNT(*) FROM OPENJSON(@Payload,''$.mappings''))>8',@Definition)=0
              OR CHARINDEX(N'(m.Slot COLLATE Latin1_General_100_BIN2=''Expense''',@Definition)=0
              THROW 50020,'Unsupported mapping validation predecessor.',1;
            SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Definition=REPLACE(@Definition,N'(SELECT COUNT(*) FROM OPENJSON(@Payload,''$.mappings''))>8',N'(SELECT COUNT(*) FROM OPENJSON(@Payload,''$.mappings''))>9');
            SET @Definition=REPLACE(@Definition,
              N'(m.Slot COLLATE Latin1_General_100_BIN2=''Expense''',
              N'(m.Slot COLLATE Latin1_General_100_BIN2=''GoodsReceivedNotInvoiced'' AND a.Type=''Liability'' AND a.Purpose=''General'') OR
                (m.Slot COLLATE Latin1_General_100_BIN2=''Expense''');
            EXEC sys.sp_executesql @Definition;
            """);

        migrationBuilder.Sql("""
            DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Accounting.Save'));
            IF @Definition IS NULL OR CHARINDEX(N'EXEC Accounting.ValidateConfiguration @TenantId,@Payload;',@Definition)=0
              THROW 50020,'Unsupported accounting save predecessor.',1;
            SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Definition=REPLACE(@Definition,
              N'EXEC Accounting.ValidateConfiguration @TenantId,@Payload;',
              N'EXEC Accounting.ValidateConfiguration @TenantId,@Payload;
                IF EXISTS(SELECT 1 FROM OPENJSON(@Payload,''$.mappings'') m
                  WHERE JSON_VALUE(m.value,''$.slot'') COLLATE Latin1_General_100_BIN2 IN (''Inventory'',''Prepayment'',''RecoverableTax'')
                  GROUP BY JSON_VALUE(m.value,''$.accountId'') HAVING COUNT(*)>1)
                  THROW 50900,''Recognition asset mappings require distinct accounts.'',1;');
            EXEC sys.sp_executesql @Definition;
            """);

        migrationBuilder.Sql($"""
            DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Security.ReadDatabaseReadiness'));
            IF @Readiness IS NULL OR CHARINDEX(N'20260925044758_AddAccountingPeriodControls',@Readiness)=0
              THROW 50020,'Unsupported purchase recognition readiness predecessor.',1;
            SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Readiness=REPLACE(@Readiness,N'20260925044758_AddAccountingPeriodControls',N'{migrationId}');
            EXEC sys.sp_executesql @Readiness;
            """);
    }
}
