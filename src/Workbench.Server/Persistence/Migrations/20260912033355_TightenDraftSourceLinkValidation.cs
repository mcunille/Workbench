// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TightenDraftSourceLinkValidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The initial purchasing migration has already been applied to a retained preview.
            // Correct future link validation without rewriting saved drafts or receipt fingerprints.
            migrationBuilder.Sql("""
                DECLARE @Definition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Purchasing].[IsDraftSourceLink]'));
                DECLARE @OldControls nvarchar(max)=N'IF @Code BETWEEN 0 AND 32 OR @Code IN (127,133,160,5760,8232,8233,8239,8287,12288) OR @Code BETWEEN 8192 AND 8202 RETURN 0;';
                DECLARE @NewControls nvarchar(max)=N'IF @Code BETWEEN 0 AND 32 OR @Code BETWEEN 127 AND 159 OR @Code IN (160,5760,8232,8233,8239,8287,12288) OR @Code BETWEEN 8192 AND 8202 RETURN 0;';
                DECLARE @HostAnchor nvarchar(max)=N'IF @End>0 SET @Port=SUBSTRING(@Authority,@End+1,20);';
                DECLARE @HostValidation nvarchar(max)=N'
                    IF LEFT(@Host,1)=N''.'' OR CHARINDEX(N''..'',@Host)>0 RETURN 0;
                    SET @I=1;
                    WHILE @I<=DATALENGTH(@Host)/2
                    BEGIN
                        SET @Code=UNICODE(SUBSTRING(@Host,@I,1));
                        IF @Code<128 AND NOT (@Code BETWEEN 45 AND 46 OR @Code BETWEEN 48 AND 57
                            OR @Code BETWEEN 65 AND 90 OR @Code=95 OR @Code BETWEEN 97 AND 122) RETURN 0;
                        SET @I+=1;
                    END;
                    '+@HostAnchor;
                IF @Definition IS NULL OR CHARINDEX(@OldControls,@Definition)=0 OR CHARINDEX(@HostAnchor,@Definition)=0
                    OR CHARINDEX(N'CREATE FUNCTION',@Definition)=0
                    THROW 50020,'The expected purchasing URL validator is required for forward correction.',1;
                SET @Definition=REPLACE(@Definition,N'CREATE FUNCTION',N'ALTER FUNCTION');
                SET @Definition=REPLACE(@Definition,@OldControls,@NewControls);
                SET @Definition=REPLACE(@Definition,@HostAnchor,@HostValidation);
                EXEC sys.sp_executesql @Definition;
                DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
                SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
                SET @Readiness=REPLACE(@Readiness,N'20260912030844_AddDraftSupplierOrders',N'20260912033355_TightenDraftSourceLinkValidation');
                EXEC sys.sp_executesql @Readiness;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("THROW 50020, 'Draft source-link validation requires forward correction or guarded recovery.', 1;");
    }
}
