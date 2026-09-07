// Copyright (c) 2026 The White Stag Collection.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddItemPhotographs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CurrentPhotoId",
                schema: "Inventory",
                table: "Items",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ItemPhotoOperations",
                schema: "Inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpectedVersion = table.Column<byte[]>(type: "varbinary(8)", maxLength: 8, nullable: false),
                    PayloadSha256 = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DetailAttachmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ThumbnailAttachmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ResultVersion = table.Column<byte[]>(type: "varbinary(8)", maxLength: 8, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemPhotoOperations", x => x.Id);
                    table.UniqueConstraint("AK_ItemPhotoOperations_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.UniqueConstraint("AK_ItemPhotoOperations_TenantId_ItemId_Id", x => new { x.TenantId, x.ItemId, x.Id });
                    table.CheckConstraint("CK_ItemPhotoOperations_Command", "[RequestId] <> '00000000-0000-0000-0000-000000000000' AND DATALENGTH([ExpectedVersion]) = 8 AND LEN([PayloadSha256]) = 64 AND (([Kind] = 0 AND [DetailAttachmentId] IS NOT NULL AND [ThumbnailAttachmentId] IS NOT NULL AND [DetailAttachmentId] <> [ThumbnailAttachmentId]) OR ([Kind] = 1 AND [DetailAttachmentId] IS NULL AND [ThumbnailAttachmentId] IS NULL))");
                    table.CheckConstraint("CK_ItemPhotoOperations_State", "[State] BETWEEN 0 AND 2 AND (([State] = 1 AND [ResultVersion] IS NOT NULL) OR ([State] <> 1 AND [ResultVersion] IS NULL))");
                    table.ForeignKey(
                        name: "FK_ItemPhotoOperations_Attachments_TenantId_DetailAttachmentId",
                        columns: x => new { x.TenantId, x.DetailAttachmentId },
                        principalSchema: "Storage",
                        principalTable: "Attachments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ItemPhotoOperations_Attachments_TenantId_ThumbnailAttachmentId",
                        columns: x => new { x.TenantId, x.ThumbnailAttachmentId },
                        principalSchema: "Storage",
                        principalTable: "Attachments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ItemPhotoOperations_Items_TenantId_ItemId",
                        columns: x => new { x.TenantId, x.ItemId },
                        principalSchema: "Inventory",
                        principalTable: "Items",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ItemPhotos",
                schema: "Inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DetailAttachmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ThumbnailAttachmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Width = table.Column<int>(type: "int", nullable: false),
                    Height = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemPhotos", x => x.Id);
                    table.UniqueConstraint("AK_ItemPhotos_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.UniqueConstraint("AK_ItemPhotos_TenantId_ItemId_Id", x => new { x.TenantId, x.ItemId, x.Id });
                    table.CheckConstraint("CK_ItemPhotos_Dimensions", "[Width] BETWEEN 1 AND 2048 AND [Height] BETWEEN 1 AND 2048 AND [DetailAttachmentId] <> [ThumbnailAttachmentId]");
                    table.ForeignKey(
                        name: "FK_ItemPhotos_Attachments_TenantId_DetailAttachmentId",
                        columns: x => new { x.TenantId, x.DetailAttachmentId },
                        principalSchema: "Storage",
                        principalTable: "Attachments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ItemPhotos_Attachments_TenantId_ThumbnailAttachmentId",
                        columns: x => new { x.TenantId, x.ThumbnailAttachmentId },
                        principalSchema: "Storage",
                        principalTable: "Attachments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ItemPhotos_ItemPhotoOperations_TenantId_ItemId_OperationId",
                        columns: x => new { x.TenantId, x.ItemId, x.OperationId },
                        principalSchema: "Inventory",
                        principalTable: "ItemPhotoOperations",
                        principalColumns: new[] { "TenantId", "ItemId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ItemPhotos_Items_TenantId_ItemId",
                        columns: x => new { x.TenantId, x.ItemId },
                        principalSchema: "Inventory",
                        principalTable: "Items",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Items_TenantId_Id_CurrentPhotoId",
                schema: "Inventory",
                table: "Items",
                columns: new[] { "TenantId", "Id", "CurrentPhotoId" });

            migrationBuilder.CreateIndex(
                name: "IX_ItemPhotoOperations_TenantId_DetailAttachmentId",
                schema: "Inventory",
                table: "ItemPhotoOperations",
                columns: new[] { "TenantId", "DetailAttachmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ItemPhotoOperations_TenantId_ItemId_RequestId",
                schema: "Inventory",
                table: "ItemPhotoOperations",
                columns: new[] { "TenantId", "ItemId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemPhotoOperations_TenantId_ThumbnailAttachmentId",
                schema: "Inventory",
                table: "ItemPhotoOperations",
                columns: new[] { "TenantId", "ThumbnailAttachmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ItemPhotos_TenantId_DetailAttachmentId",
                schema: "Inventory",
                table: "ItemPhotos",
                columns: new[] { "TenantId", "DetailAttachmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemPhotos_TenantId_ItemId_OperationId",
                schema: "Inventory",
                table: "ItemPhotos",
                columns: new[] { "TenantId", "ItemId", "OperationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ItemPhotos_TenantId_OperationId",
                schema: "Inventory",
                table: "ItemPhotos",
                columns: new[] { "TenantId", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemPhotos_TenantId_ThumbnailAttachmentId",
                schema: "Inventory",
                table: "ItemPhotos",
                columns: new[] { "TenantId", "ThumbnailAttachmentId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Items_ItemPhotos_TenantId_Id_CurrentPhotoId",
                schema: "Inventory",
                table: "Items",
                columns: new[] { "TenantId", "Id", "CurrentPhotoId" },
                principalSchema: "Inventory",
                principalTable: "ItemPhotos",
                principalColumns: new[] { "TenantId", "ItemId", "Id" },
                onDelete: ReferentialAction.Restrict);
            ItemPhotoSchema.Protect(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("THROW 50020, 'Item photographs and operation history require a forward correction or paired offline recovery.', 1;");
        }
    }
}
