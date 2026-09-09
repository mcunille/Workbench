// Copyright (c) 2026 The White Stag Collection.

using Microsoft.EntityFrameworkCore.Migrations;

namespace Workbench.Server.Persistence.Migrations;

public partial class AddAcquisitionContext
{
    private static void AddCommands(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[] { "Acquisitions", "AcquisitionItems", "AcquisitionCreationRecords" })
            migrationBuilder.Sql($"""
                ALTER SECURITY POLICY [Security].[TenantIsolationPolicy]
                    ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Inventory].[{table}],
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Inventory].[{table}] AFTER INSERT,
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Inventory].[{table}] AFTER UPDATE;
                GRANT SELECT ON [Inventory].[{table}] TO [workbench_web];
                DENY INSERT, UPDATE, DELETE ON [Inventory].[{table}] TO [workbench_web];
                """);
        migrationBuilder.Sql($"""
            CREATE PROCEDURE [Inventory].[CreateAcquisition]
                @ItemId uniqueidentifier, @CreationRequestId uniqueidentifier, @ExpectedItemVersion varbinary(max),
                @Method nvarchar(max), @Source nvarchar(max)=NULL, @Year int=NULL, @Month int=NULL,
                @Day int=NULL, @Notes nvarchar(max)=NULL
            AS
            BEGIN
                SET NOCOUNT ON;
                SET XACT_ABORT ON;
                {ValidateFields}
                IF @CreationRequestId IS NULL OR @CreationRequestId='00000000-0000-0000-0000-000000000000'
                    THROW 50045, 'A creation request identifier is required.', 1;
                DECLARE @TenantId uniqueidentifier, @Version binary(8), @Archived datetimeoffset;
                SELECT @TenantId=TenantId,@Version=RowVersion,@Archived=ArchivedAtUtc
                    FROM [Inventory].[Items] WITH (UPDLOCK,HOLDLOCK) WHERE Id=@ItemId;
                IF @TenantId IS NULL BEGIN SELECT 0; RETURN; END;
                -- Item first, then a tenant/request lock serializes reuse against different items.
                DECLARE @LockResult int, @Resource nvarchar(255)=CONCAT(N'Acquisition:',@TenantId,':',@CreationRequestId);
                EXEC @LockResult=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=30000;
                IF @LockResult<0 THROW 50046, 'Acquisition request lock unavailable.', 1;
                IF EXISTS (SELECT 1 FROM [Inventory].[AcquisitionCreationRecords]
                    WHERE TenantId=@TenantId AND CreationRequestId=@CreationRequestId)
                BEGIN
                    -- Compare immutable normalized UTF-16 bytes, including case and trailing notes spaces.
                    IF EXISTS (SELECT 1 FROM [Inventory].[AcquisitionCreationRecords]
                        WHERE TenantId=@TenantId AND CreationRequestId=@CreationRequestId AND ItemId=@ItemId
                        AND CONVERT(varbinary(max),Method)=CONVERT(varbinary(max),@Method)
                        AND ((Source IS NULL AND @Source IS NULL) OR CONVERT(varbinary(max),Source)=CONVERT(varbinary(max),@Source))
                        AND ((Notes IS NULL AND @Notes IS NULL) OR CONVERT(varbinary(max),Notes)=CONVERT(varbinary(max),@Notes))
                        AND (([Year] IS NULL AND @Year IS NULL) OR [Year]=@Year)
                        AND (([Month] IS NULL AND @Month IS NULL) OR [Month]=@Month)
                        AND (([Day] IS NULL AND @Day IS NULL) OR [Day]=@Day)) SELECT 4;
                    ELSE SELECT 5;
                    RETURN;
                END;
                IF @Archived IS NOT NULL BEGIN SELECT 3; RETURN; END;
                IF EXISTS (SELECT 1 FROM [Inventory].[AcquisitionItems] WHERE TenantId=@TenantId AND ItemId=@ItemId)
                    BEGIN SELECT 6; RETURN; END;
                IF @Version<>@ExpectedItemVersion BEGIN SELECT 2; RETURN; END;
                DECLARE @Id uniqueidentifier=NEWID();
                INSERT [Inventory].[Acquisitions] (Id,TenantId,Method,Source,[Year],[Month],[Day],Notes,CreatedAtUtc,CreationRequestId)
                    VALUES (@Id,@TenantId,@Method,@Source,@Year,@Month,@Day,@Notes,SYSUTCDATETIME(),@CreationRequestId);
                INSERT [Inventory].[AcquisitionItems] (TenantId,ItemId,AcquisitionId) VALUES (@TenantId,@ItemId,@Id);
                INSERT [Inventory].[AcquisitionCreationRecords] (TenantId,CreationRequestId,AcquisitionId,ItemId,Method,Source,[Year],[Month],[Day],Notes)
                    VALUES (@TenantId,@CreationRequestId,@Id,@ItemId,@Method,@Source,@Year,@Month,@Day,@Notes);
                UPDATE [Inventory].[Items] SET Name=Name WHERE TenantId=@TenantId AND Id=@ItemId;
                SELECT 1;
            END;
            """);
        migrationBuilder.Sql($"""
            CREATE PROCEDURE [Inventory].[UpdateAcquisition]
                @ItemId uniqueidentifier, @AcquisitionId uniqueidentifier, @ExpectedItemVersion varbinary(max),
                @ExpectedAcquisitionVersion varbinary(max), @Method nvarchar(max), @Source nvarchar(max)=NULL,
                @Year int=NULL, @Month int=NULL, @Day int=NULL, @Notes nvarchar(max)=NULL
            AS
            BEGIN
                SET NOCOUNT ON;
                SET XACT_ABORT ON;
                {ValidateFields}
                IF @ExpectedAcquisitionVersion IS NULL OR DATALENGTH(@ExpectedAcquisitionVersion)<>8
                    THROW 50045, 'An eight-byte acquisition version is required.', 1;
                DECLARE @TenantId uniqueidentifier, @Version binary(8), @Archived datetimeoffset;
                SELECT @TenantId=TenantId,@Version=RowVersion,@Archived=ArchivedAtUtc
                    FROM [Inventory].[Items] WITH (UPDLOCK,HOLDLOCK) WHERE Id=@ItemId;
                IF @TenantId IS NULL OR NOT EXISTS (SELECT 1 FROM [Inventory].[AcquisitionItems]
                    WHERE TenantId=@TenantId AND ItemId=@ItemId AND AcquisitionId=@AcquisitionId)
                    BEGIN SELECT 0; RETURN; END;
                IF @Archived IS NOT NULL BEGIN SELECT 3; RETURN; END;
                IF @Version<>@ExpectedItemVersion BEGIN SELECT 2; RETURN; END;
                UPDATE [Inventory].[Acquisitions] SET Method=@Method,Source=@Source,[Year]=@Year,[Month]=@Month,[Day]=@Day,Notes=@Notes
                    WHERE TenantId=@TenantId AND Id=@AcquisitionId AND RowVersion=@ExpectedAcquisitionVersion;
                IF @@ROWCOUNT<>1 BEGIN SELECT 2; RETURN; END;
                UPDATE [Inventory].[Items] SET Name=Name WHERE TenantId=@TenantId AND Id=@ItemId;
                SELECT 1;
            END;
            """);
        migrationBuilder.Sql("""
            GRANT EXECUTE ON [Inventory].[CreateAcquisition] TO [workbench_web];
            GRANT EXECUTE ON [Inventory].[UpdateAcquisition] TO [workbench_web];
            DECLARE @Readiness nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'[Security].[ReadDatabaseReadiness]'));
            SET @Readiness=REPLACE(@Readiness,N'CREATE PROCEDURE',N'ALTER PROCEDURE');
            SET @Readiness=REPLACE(@Readiness,N'20260908010000_AddItemRestoration',N'20260909034719_AddAcquisitionContext');
            EXEC sys.sp_executesql @Readiness;
            """);
    }

    private const string ValidateFields = """
        IF @@TRANCOUNT=0 OR @ExpectedItemVersion IS NULL OR DATALENGTH(@ExpectedItemVersion)<>8
            THROW 50045, 'Acquisition commands require a transaction and an eight-byte item version.', 1;
        DECLARE @Whitespace nvarchar(64)=NCHAR(9)+NCHAR(10)+NCHAR(11)+NCHAR(12)+NCHAR(13)+NCHAR(32)+NCHAR(133)+NCHAR(160)+NCHAR(5760)+NCHAR(8192)+NCHAR(8193)+NCHAR(8194)+NCHAR(8195)+NCHAR(8196)+NCHAR(8197)+NCHAR(8198)+NCHAR(8199)+NCHAR(8200)+NCHAR(8201)+NCHAR(8202)+NCHAR(8232)+NCHAR(8233)+NCHAR(8239)+NCHAR(8287)+NCHAR(12288);
        SET @Source=NULLIF(TRIM(@Whitespace FROM @Source),N'');
        IF LEN(TRIM(@Whitespace FROM @Notes))=0 SET @Notes=NULL;
        -- Wide parameters validate before conversion to table column sizes.
        IF @Method IS NULL OR @Method COLLATE Latin1_General_100_BIN2 NOT IN (N'Purchase',N'Gift',N'Inheritance',N'Trade',N'Other',N'Unknown')
            OR DATALENGTH(@Method)<>DATALENGTH(RTRIM(@Method)) OR DATALENGTH(@Method)>32
            OR DATALENGTH(@Source)>400 OR DATALENGTH(@Notes)>8000
            THROW 50045, 'Invalid acquisition fields.', 1;
        IF (@Year IS NULL AND (@Month IS NOT NULL OR @Day IS NOT NULL)) OR @Year NOT BETWEEN 1 AND 9999
            OR (@Month IS NULL AND @Day IS NOT NULL) OR @Month NOT BETWEEN 1 AND 12 OR @Day NOT BETWEEN 1 AND 31
            THROW 50045, 'Invalid acquisition date.', 1;
        IF @Year IS NOT NULL
        BEGIN
            DECLARE @Earliest date=TRY_CONVERT(date,CONCAT(RIGHT('0000'+CONVERT(varchar(4),@Year),4),
                RIGHT('00'+CONVERT(varchar(2),COALESCE(@Month,1)),2),RIGHT('00'+CONVERT(varchar(2),COALESCE(@Day,1)),2)),112);
            IF @Earliest IS NULL OR @Earliest>CONVERT(date,SYSUTCDATETIME())
                THROW 50045, 'Acquisition date must be valid and cannot be in the future.', 1;
        END;
        """;
}
