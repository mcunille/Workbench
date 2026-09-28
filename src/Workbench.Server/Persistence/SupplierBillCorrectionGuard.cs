// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence;

internal static class SupplierBillCorrectionGuard
{
    internal static void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.CorrectRecognition')),
              @Anchor nvarchar(max)=N'DECLARE @Originals TABLE(';
            IF @Definition IS NULL OR CHARINDEX(@Anchor,@Definition)=0 THROW 50020,'Unsupported bill correction predecessor.',1;
            SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Definition=REPLACE(@Definition,@Anchor,N'
              IF EXISTS(SELECT 1 FROM Purchasing.SupplierBillPostingEvents b JOIN Purchasing.RecognitionSideEvents e
                ON e.TenantId=b.TenantId AND e.Id=b.EventId WHERE b.TenantId=@TenantId AND e.UnitId=@UnitId)
                THROW 51009,''Bill-owned recognition requires a complete bill correction adapter.'',1;
              '+@Anchor);
            EXEC sys.sp_executesql @Definition;
            """);
        migrationBuilder.Sql("""
            DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Accounting.CorrectJournal')),
              @Anchor nvarchar(max)=N'IF @OriginalEventId IS NULL';
            IF @Definition IS NULL OR CHARINDEX(@Anchor,@Definition)=0 THROW 50020,'Unsupported bill journal correction predecessor.',1;
            SET @Definition=REPLACE(@Definition,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Definition=REPLACE(@Definition,@Anchor,N'
              IF EXISTS(SELECT 1 FROM Purchasing.RecognitionSideEvents target JOIN Purchasing.RecognitionSideEvents owned
                ON owned.TenantId=target.TenantId AND owned.UnitId=target.UnitId JOIN Purchasing.SupplierBillPostingEvents bill
                ON bill.TenantId=owned.TenantId AND bill.EventId=owned.Id WHERE target.TenantId=@TenantId AND target.JournalId=@OriginalJournalId)
                THROW 51009,''Bill-owned journals require a complete bill correction adapter.'',1;
              '+@Anchor);
            EXEC sys.sp_executesql @Definition;
            """);
    }
}
