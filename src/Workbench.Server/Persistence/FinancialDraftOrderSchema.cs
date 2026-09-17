// Copyright (c) 2026 The White Stag Collection.
using System.Text;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Workbench.Server.Persistence;

internal static partial class FinancialDraftOrderSchema
{
    internal const string MigrationId = "20260917020000_AddDraftFinancialAdjustments";

    internal static void Protect(MigrationBuilder migrationBuilder)
    {
        // Derive the forward command from the installed immutable predecessor. Exact anchors fail
        // closed if the supported predecessor changes; historical migrations remain untouched.
        var sql = new StringBuilder("DECLARE @Command nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Purchasing].[SaveDraftOrderV4]'));\n");
        void Replace(string before, string after)
        {
            static string Literal(string text) => "N'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
            sql.AppendLine($"IF @Command IS NULL OR CHARINDEX({Literal(before)},@Command)=0 THROW 50020,'Unsupported financial command predecessor.',1;");
            sql.AppendLine($"SET @Command=REPLACE(@Command,{Literal(before)},{Literal(after)});");
        }
        Replace("CREATE PROCEDURE", "ALTER PROCEDURE");
        Replace("FROM @Fields)<>14", "FROM @Fields)<>16");
        Replace("CONVERT(varbinary(max),N'platform')))", "CONVERT(varbinary(max),N'platform'),CONVERT(varbinary(max),N'orderDiscount'),CONVERT(varbinary(max),N'charges')))");
        Replace("([key] IN(N'sourceLinks',N'entries') AND [type]<>4) OR ([key] NOT IN(N'sourceLinks',N'entries') AND [type] NOT IN(0,1))",
            "([key] IN(N'sourceLinks',N'entries',N'charges') AND [type]<>4) OR ([key]=N'orderDiscount' AND [type] NOT IN(0,5)) OR ([key] NOT IN(N'sourceLinks',N'entries',N'charges',N'orderDiscount') AND [type] NOT IN(0,1))");
        Replace("p.EntryIndex=CONVERT(int,e.[key]))<>12", "p.EntryIndex=CONVERT(int,e.[key]))<>13");
        Replace("CONVERT(varbinary(max),N'itemType')) OR ([key]<>N'legacyPricing' AND [type] NOT IN(0,1)) OR ([key]=N'legacyPricing' AND [type] NOT IN(0,5))",
            "CONVERT(varbinary(max),N'itemType'),CONVERT(varbinary(max),N'discount')) OR ([key] NOT IN(N'legacyPricing',N'discount') AND [type] NOT IN(0,1)) OR ([key] IN(N'legacyPricing',N'discount') AND [type] NOT IN(0,5))");
        Replace("DECLARE @Content nvarchar(max)=N'{\"sourceLinks\":'+@Links+N',\"entries\":'+@Entries+N'}';",
            ValidateAdjustments + "\nDECLARE @Content nvarchar(max)=N'{\"sourceLinks\":'+@Links+N',\"entries\":'+@Entries+N',\"orderDiscount\":'+COALESCE(@OrderDiscount,N'null')+N',\"charges\":'+@Charges+N'}';");
        Replace("IF @CurrentVersion<>@ExpectedVersion THROW 50409,'The draft changed. Review the current version.',1;",
            "IF @CurrentVersion<>@ExpectedVersion THROW 50409,'The draft changed. Review the current version.',1;\n" + ValidateTransition);
        Replace("ContentSchemaVersion=3,ContentJson=@Content", "ContentSchemaVersion=4,ContentJson=@Content");
        Replace("@Currency,@Notes,3,@Content", "@Currency,@Notes,4,@Content");
        sql.AppendLine("EXEC sys.sp_executesql @Command;");
        migrationBuilder.Sql(sql.ToString());
        migrationBuilder.Sql($"""
            DECLARE @Old nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Purchasing].[SaveDraftOrderV3]'));
            SET @Old=REPLACE(@Old,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Old=REPLACE(@Old,N'ContentSchemaVersion=3',N'ContentSchemaVersion>=3');
            EXEC sys.sp_executesql @Old;
            DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Readiness=REPLACE(@Readiness,N'20260917010000_AddSupplierBasedDraftPricing',N'{MigrationId}');
            EXEC sys.sp_executesql @Readiness;
            GRANT EXECUTE ON [Purchasing].[CreateDraftOrderV4] TO [workbench_web];
            GRANT EXECUTE ON [Purchasing].[UpdateDraftOrderV4] TO [workbench_web];
            """);
    }

    private const string ValidateTransition = """
        DECLARE @SavedContent nvarchar(max);
        SELECT @SavedContent=ContentJson FROM Purchasing.DraftOrders WHERE TenantId=@TenantId AND Id=@TargetId;
        IF @CurrentCurrency IS NOT NULL AND (@Currency IS NULL OR CONVERT(varbinary(max),CONVERT(nvarchar(3),@CurrentCurrency))<>CONVERT(varbinary(max),@Currency))
            AND (EXISTS(SELECT 1 FROM @Discounts WHERE Mode=N'fixed') OR EXISTS(SELECT 1 FROM @ChargeRows WHERE Amount IS NOT NULL)
                OR JSON_VALUE(@SavedContent,'$.orderDiscount.mode')=N'fixed'
                OR EXISTS(SELECT 1 FROM OPENJSON(@SavedContent,'$.entries') WHERE JSON_VALUE([value],'$.discount.mode')=N'fixed')
                OR EXISTS(SELECT 1 FROM OPENJSON(@SavedContent,'$.charges') WHERE JSON_VALUE([value],'$.amount') IS NOT NULL))
            THROW 50401,'Clear amounts before changing currency.',1;
        IF EXISTS(SELECT 1 FROM OPENJSON(@SavedContent,'$.charges') old
            JOIN @ChargeRows currentRow ON currentRow.Id=TRY_CONVERT(uniqueidentifier,JSON_VALUE(old.[value],'$.id'))
            WHERE JSON_VALUE(old.[value],'$.amountStatus')=N'confirmed'
                AND (COALESCE(JSON_VALUE(old.[value],'$.amount'),N'')<>COALESCE(currentRow.AmountText,N'')
                    OR CONVERT(varbinary(max),COALESCE(JSON_VALUE(old.[value],'$.payeeKind'),N''))<>CONVERT(varbinary(max),currentRow.PayeeKind)
                    OR CONVERT(varbinary(max),COALESCE(JSON_VALUE(old.[value],'$.payeeName'),N''))<>CONVERT(varbinary(max),COALESCE(currentRow.PayeeName,N''))
                    OR currentRow.AmountStatus<>N'confirmed')
                AND (currentRow.Notes IS NULL OR CONVERT(varbinary(max),currentRow.Notes)=CONVERT(varbinary(max),COALESCE(JSON_VALUE(old.[value],'$.notes'),N''))))
            THROW 50400,'Explain the confirmed charge correction with new notes.',1;
        """;
}
