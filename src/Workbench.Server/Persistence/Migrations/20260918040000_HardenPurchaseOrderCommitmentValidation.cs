// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;
namespace Workbench.Server.Persistence.Migrations;

public partial class HardenPurchaseOrderCommitmentValidation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The preceding migration was applied to a retained isolated preview. Preserve its
        // exact command and add this forward correction rather than rewriting applied history.
        migrationBuilder.Sql("""
            DECLARE @Command nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Purchasing.SavePurchaseOrder'));
            IF @Command IS NULL OR CHARINDEX(N'DECLARE @SavedDraft nvarchar(max)=',@Command)=0
                OR CHARINDEX(N'DECLARE @Canonical nvarchar(max)=',@Command)=0 THROW 50020,'Unsupported commitment validation predecessor.',1;
            SET @Command=REPLACE(@Command,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Command=REPLACE(@Command,N'SupplierId supplierId,SupplierContactName supplierContactName',N'LOWER(CONVERT(varchar(36),SupplierId)) supplierId,SupplierContactName supplierContactName');
            SET @Command=REPLACE(@Command,N'DECLARE @Canonical nvarchar(max)=',N'
                DECLARE @Whitespace nvarchar(64)=NCHAR(9)+NCHAR(10)+NCHAR(11)+NCHAR(12)+NCHAR(13)+NCHAR(32)+NCHAR(133)+NCHAR(160)+NCHAR(5760)+NCHAR(8192)+NCHAR(8193)+NCHAR(8194)+NCHAR(8195)+NCHAR(8196)+NCHAR(8197)+NCHAR(8198)+NCHAR(8199)+NCHAR(8200)+NCHAR(8201)+NCHAR(8202)+NCHAR(8232)+NCHAR(8233)+NCHAR(8239)+NCHAR(8287)+NCHAR(12288);
                IF @Operation=''Amend'' AND (DATALENGTH(TRIM(@Whitespace FROM @Reason))=0 OR CONVERT(varbinary(max),@Reason)<>CONVERT(varbinary(max),TRIM(@Whitespace FROM @Reason))) THROW 50400,''Enter a trimmed amendment reason.'',1;
                DECLARE @Canonical nvarchar(max)=');
            SET @Command=REPLACE(@Command,N'DECLARE @SavedDraft nvarchar(max)=',N'
                DECLARE @SavedEntries nvarchar(max),@StoredSchema smallint;
                SELECT @SavedEntries=JSON_QUERY(ContentJson,''$.entries''),@StoredSchema=ContentSchemaVersion FROM Purchasing.DraftOrders WHERE TenantId=@TenantId AND Id=@TargetId;
                IF @StoredSchema=3
                BEGIN
                    DECLARE @EntryIndex int=0,@EntryCount int=(SELECT COUNT(*) FROM OPENJSON(@SavedEntries)),@Path nvarchar(100),@Entry nvarchar(max);
                    WHILE @EntryIndex<@EntryCount
                    BEGIN
                        SET @Path=N''$[''+CONVERT(nvarchar(10),@EntryIndex)+N'']'';
                        SET @Entry=JSON_QUERY(@SavedEntries,@Path);
                        IF NOT EXISTS(SELECT 1 FROM OPENJSON(@Entry) WHERE [key]=N''discount'')
                            SET @Entry=LEFT(@Entry,LEN(@Entry)-1)+N'','' + N''"discount":null}'';
                        SET @SavedEntries=JSON_MODIFY(@SavedEntries,@Path,JSON_QUERY(@Entry));
                        SET @EntryIndex+=1;
                    END;
                END;
                DECLARE @SavedDraft nvarchar(max)=');
            SET @Command=REPLACE(@Command,N'JSON_QUERY(ContentJson,''$.entries'') entries',N'JSON_QUERY(@SavedEntries) entries');
            EXEC sys.sp_executesql @Command;
            DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'Security.ReadDatabaseReadiness'));
            SET @Readiness=REPLACE(REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE'),N'20260918030000_AddPurchaseOrderCommitment',N'20260918040000_HardenPurchaseOrderCommitmentValidation');
            EXEC sys.sp_executesql @Readiness;
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("THROW 50020, 'Purchase history validation requires forward correction or guarded recovery.', 1;");
}
