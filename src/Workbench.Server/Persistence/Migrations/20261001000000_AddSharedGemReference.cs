// Copyright (c) 2026 The White Stag Collection.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedGemReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "Gemology");

            migrationBuilder.CreateTable(
                name: "Entries",
                schema: "Gemology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MaterialKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CommonName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Group = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Species = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Variety = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IdentityKey = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    IsRetired = table.Column<bool>(type: "bit", nullable: false),
                    RetirementExplanation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RedirectEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Entries", x => x.Id);
                    table.CheckConstraint("CK_GemEntries_Id", "[Id]<>'00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_GemEntries_Kind", "[MaterialKind] COLLATE Latin1_General_100_BIN2 IN ('mineral','mineraloid','organic','rockAggregate')");
                    table.CheckConstraint("CK_GemEntries_Name", "LEN(LTRIM(RTRIM([CommonName])))>0");
                    table.CheckConstraint("CK_GemEntries_OptionalText", "([Group] IS NULL OR LEN(LTRIM(RTRIM([Group])))>0) AND ([Species] IS NULL OR LEN(LTRIM(RTRIM([Species])))>0) AND ([Variety] IS NULL OR LEN(LTRIM(RTRIM([Variety])))>0) AND ([Description] IS NULL OR LEN(LTRIM(RTRIM([Description])))>0) AND ([RetirementExplanation] IS NULL OR LEN(LTRIM(RTRIM([RetirementExplanation])))>0)");
                    table.CheckConstraint("CK_GemEntries_Redirect", "[RedirectEntryId] IS NULL OR [RedirectEntryId]<>[Id]");
                    table.CheckConstraint("CK_GemEntries_Retirement", "([IsRetired]=0 AND [RedirectEntryId] IS NULL AND [RetirementExplanation] IS NULL) OR ([IsRetired]=1 AND ([RetirementExplanation] IS NOT NULL OR [RedirectEntryId] IS NOT NULL))");
                    table.CheckConstraint("CK_GemEntries_Species", "[MaterialKind] COLLATE Latin1_General_100_BIN2<>'mineral' OR ([Species] IS NOT NULL AND LEN(LTRIM(RTRIM([Species])))>0)");
                    table.ForeignKey(
                        name: "FK_Entries_Entries_RedirectEntryId",
                        column: x => x.RedirectEntryId,
                        principalSchema: "Gemology",
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Aliases",
                schema: "Gemology",
                columns: table => new
                {
                    EntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    NormalizedKey = table.Column<byte[]>(type: "binary(32)", nullable: false, computedColumnSql: "CONVERT(binary(32), HASHBYTES('SHA2_256', [NormalizedName]))", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Aliases", x => new { x.EntryId, x.Position });
                    table.CheckConstraint("CK_GemAliases_Name", "LEN(LTRIM(RTRIM([Name])))>0 AND LEN([NormalizedName])>0");
                    table.CheckConstraint("CK_GemAliases_Position", "[Position]>=0 AND [Position]<20");
                    table.ForeignKey(
                        name: "FK_Aliases_Entries_EntryId",
                        column: x => x.EntryId,
                        principalSchema: "Gemology",
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceAssertions",
                schema: "Gemology",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Field = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Publisher = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Citation = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    AccessedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ReviewedOn = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceAssertions", x => x.Id);
                    table.UniqueConstraint("AK_SourceAssertions_EntryId_Id_Field_ReviewedOn", x => new { x.EntryId, x.Id, x.Field, x.ReviewedOn });
                    table.CheckConstraint("CK_GemSources_Dates", "[ReviewedOn]>'0001-01-01' AND ([AccessedOn] IS NULL OR [AccessedOn]>'0001-01-01')");
                    table.CheckConstraint("CK_GemSources_Field", "[Field] COLLATE Latin1_General_100_BIN2 IN ('materialKind','commonName','aliases','group','species','variety','description','notableLocality')");
                    table.CheckConstraint("CK_GemSources_Id", "[Id]<>'00000000-0000-0000-0000-000000000000'");
                    table.CheckConstraint("CK_GemSources_Identification", "LEN(LTRIM(RTRIM([Title])))>0 AND LEN(LTRIM(RTRIM([Publisher])))>0 AND (([Url] IS NOT NULL AND LEN(LTRIM(RTRIM([Url])))>0) OR ([Citation] IS NOT NULL AND LEN(LTRIM(RTRIM([Citation])))>0))");
                    table.ForeignKey(
                        name: "FK_SourceAssertions_Entries_EntryId",
                        column: x => x.EntryId,
                        principalSchema: "Gemology",
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LocalityAssertions",
                schema: "Gemology",
                columns: table => new
                {
                    EntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Place = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ReviewedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    SourceAssertionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceField = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalityAssertions", x => x.EntryId);
                    table.CheckConstraint("CK_GemLocality_Claim", "LEN(LTRIM(RTRIM([Place])))>0 AND LEN(LTRIM(RTRIM([Scope])))>0 AND [SourceField] COLLATE Latin1_General_100_BIN2='notableLocality'");
                    table.ForeignKey(
                        name: "FK_LocalityAssertions_Entries_EntryId",
                        column: x => x.EntryId,
                        principalSchema: "Gemology",
                        principalTable: "Entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LocalityAssertions_SourceAssertions_EntryId_SourceAssertionId_SourceField_ReviewedOn",
                        columns: x => new { x.EntryId, x.SourceAssertionId, x.SourceField, x.ReviewedOn },
                        principalSchema: "Gemology",
                        principalTable: "SourceAssertions",
                        principalColumns: new[] { "EntryId", "Id", "Field", "ReviewedOn" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Aliases_EntryId_NormalizedKey",
                schema: "Gemology",
                table: "Aliases",
                columns: new[] { "EntryId", "NormalizedKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Entries_CommonName_Id",
                schema: "Gemology",
                table: "Entries",
                columns: new[] { "CommonName", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Entries_IdentityKey",
                schema: "Gemology",
                table: "Entries",
                column: "IdentityKey",
                unique: true,
                filter: "[IsRetired] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_Entries_RedirectEntryId",
                schema: "Gemology",
                table: "Entries",
                column: "RedirectEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_LocalityAssertions_EntryId_SourceAssertionId_SourceField_ReviewedOn",
                schema: "Gemology",
                table: "LocalityAssertions",
                columns: new[] { "EntryId", "SourceAssertionId", "SourceField", "ReviewedOn" });
            GemReferenceSchema.Up(migrationBuilder, "20261001000000_AddSharedGemReference");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("THROW 50020, 'Shared gem reference identities require forward correction or guarded recovery.', 1;");
        }
    }
}

