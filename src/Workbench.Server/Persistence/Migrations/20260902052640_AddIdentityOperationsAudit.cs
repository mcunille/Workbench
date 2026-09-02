// Copyright (c) 2026 The White Stag Collection.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentityOperationsAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ActorUserId",
                schema: "Security",
                table: "TenantSecurityAuditEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                schema: "Security",
                table: "TenantSecurityAuditEvents",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetadataJson",
                schema: "Security",
                table: "TenantSecurityAuditEvents",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                schema: "Security",
                table: "TenantSecurityAuditEvents",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Succeeded");

            migrationBuilder.AddColumn<Guid>(
                name: "TargetId",
                schema: "Security",
                table: "TenantSecurityAuditEvents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetType",
                schema: "Security",
                table: "TenantSecurityAuditEvents",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IdentityOperations",
                schema: "Identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Purpose = table.Column<int>(type: "int", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    SecurityVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConsumedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityOperations", x => x.Id);
                    table.UniqueConstraint("AK_IdentityOperations_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_IdentityOperations_Users_TenantId_UserId",
                        columns: x => new { x.TenantId, x.UserId },
                        principalSchema: "Identity",
                        principalTable: "Users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SystemSecurityAuditEvents",
                schema: "Security",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    MetadataJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSecurityAuditEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityOperations_TenantId_UserId",
                schema: "Identity",
                table: "IdentityOperations",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityOperations_TokenHash",
                schema: "Identity",
                table: "IdentityOperations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.Sql("""
                ALTER SECURITY POLICY [Security].[TenantIsolationPolicy]
                    ADD FILTER PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[IdentityOperations],
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[IdentityOperations] AFTER INSERT,
                    ADD BLOCK PREDICATE [Security].[fn_tenant_access]([TenantId]) ON [Identity].[IdentityOperations] AFTER UPDATE;
                """);

            migrationBuilder.Sql("""
                CREATE PROCEDURE [Identity].[ResolveRecoveryTarget]
                    @NormalizedEmail nvarchar(256)
                WITH EXECUTE AS OWNER
                AS
                BEGIN
                    SET NOCOUNT ON;
                    SELECT TOP (1) [user].[Id], [user].[TenantId], [user].[SecurityVersion], [user].[Email]
                    FROM [Identity].[LoginDirectory] AS [directory]
                    INNER JOIN [Identity].[Users] AS [user]
                        ON [user].[TenantId] = [directory].[TenantId] AND [user].[Id] = [directory].[UserId]
                    INNER JOIN [Tenancy].[Tenants] AS [tenant] ON [tenant].[Id] = [user].[TenantId]
                    WHERE [directory].[NormalizedEmail] = @NormalizedEmail
                        AND [user].[State] = 1 AND [tenant].[IsEnabled] = 1 AND [user].[Email] IS NOT NULL;
                END;
                """);

            migrationBuilder.Sql("""
                CREATE PROCEDURE [Identity].[ResolveOperationAuthority]
                    @TokenHash binary(32)
                WITH EXECUTE AS OWNER
                AS
                BEGIN
                    SET NOCOUNT ON;
                    SELECT TOP (1) [TenantId], [UserId]
                    FROM [Identity].[IdentityOperations]
                    WHERE [TokenHash] = @TokenHash;
                END;
                """);

            migrationBuilder.Sql("""
                CREATE PROCEDURE [Identity].[CreateInvitation]
                    @TenantId uniqueidentifier,
                    @UserId uniqueidentifier,
                    @OperationId uniqueidentifier,
                    @Email nvarchar(256),
                    @NormalizedEmail nvarchar(256),
                    @TokenHash binary(32),
                    @Now datetimeoffset,
                    @Expires datetimeoffset
                WITH EXECUTE AS OWNER
                AS
                BEGIN
                    SET NOCOUNT ON;
                    SET XACT_ABORT ON;
                    IF TRY_CONVERT(uniqueidentifier, SESSION_CONTEXT(N'TenantId')) <> @TenantId
                        THROW 50001, 'Tenant context mismatch.', 1;

                    BEGIN TRANSACTION;
                    INSERT INTO [Identity].[Users]
                        ([Id], [TenantId], [SecurityVersion], [State], [CreatedAtUtc],
                         [UserName], [NormalizedUserName], [Email], [NormalizedEmail], [EmailConfirmed],
                         [PhoneNumberConfirmed], [TwoFactorEnabled], [LockoutEnabled], [AccessFailedCount])
                    VALUES
                        (@UserId, @TenantId, 1, 3, @Now,
                         @Email, @NormalizedEmail, @Email, @NormalizedEmail, 0, 0, 0, 0, 0);
                    INSERT INTO [Identity].[LoginDirectory] ([NormalizedEmail], [UserId], [TenantId])
                    VALUES (@NormalizedEmail, @UserId, @TenantId);
                    INSERT INTO [Identity].[IdentityOperations]
                        ([Id], [TenantId], [UserId], [Purpose], [TokenHash], [SecurityVersion],
                         [CreatedAtUtc], [ExpiresAtUtc])
                    VALUES (@OperationId, @TenantId, @UserId, 2, @TokenHash, 1, @Now, @Expires);
                    COMMIT TRANSACTION;
                END;
                """);

            migrationBuilder.Sql("""
                GRANT EXECUTE ON [Identity].[ResolveRecoveryTarget] TO [workbench_web];
                GRANT EXECUTE ON [Identity].[ResolveOperationAuthority] TO [workbench_web];
                GRANT EXECUTE ON [Identity].[CreateInvitation] TO [workbench_web];
                GRANT SELECT, INSERT, UPDATE ON [Identity].[IdentityOperations] TO [workbench_web];
                GRANT SELECT, INSERT ON [Security].[TenantSecurityAuditEvents] TO [workbench_web];
                DENY UPDATE, DELETE ON [Security].[TenantSecurityAuditEvents] TO [workbench_web];
                DENY SELECT, INSERT, UPDATE, DELETE ON [Security].[SystemSecurityAuditEvents] TO [workbench_web];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP PROCEDURE [Identity].[ResolveRecoveryTarget];
                DROP PROCEDURE [Identity].[ResolveOperationAuthority];
                DROP PROCEDURE [Identity].[CreateInvitation];
                ALTER SECURITY POLICY [Security].[TenantIsolationPolicy]
                    DROP FILTER PREDICATE ON [Identity].[IdentityOperations],
                    DROP BLOCK PREDICATE ON [Identity].[IdentityOperations] AFTER INSERT,
                    DROP BLOCK PREDICATE ON [Identity].[IdentityOperations] AFTER UPDATE;
                """);

            migrationBuilder.DropTable(
                name: "IdentityOperations",
                schema: "Identity");

            migrationBuilder.DropTable(
                name: "SystemSecurityAuditEvents",
                schema: "Security");

            migrationBuilder.DropColumn(
                name: "ActorUserId",
                schema: "Security",
                table: "TenantSecurityAuditEvents");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                schema: "Security",
                table: "TenantSecurityAuditEvents");

            migrationBuilder.DropColumn(
                name: "MetadataJson",
                schema: "Security",
                table: "TenantSecurityAuditEvents");

            migrationBuilder.DropColumn(
                name: "Outcome",
                schema: "Security",
                table: "TenantSecurityAuditEvents");

            migrationBuilder.DropColumn(
                name: "TargetId",
                schema: "Security",
                table: "TenantSecurityAuditEvents");

            migrationBuilder.DropColumn(
                name: "TargetType",
                schema: "Security",
                table: "TenantSecurityAuditEvents");
        }
    }
}
