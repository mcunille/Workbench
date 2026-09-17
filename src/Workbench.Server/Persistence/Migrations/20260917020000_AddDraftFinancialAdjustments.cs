// Copyright (c) 2026 The White Stag Collection.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations;

public partial class AddDraftFinancialAdjustments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(name: "CK_DraftOrders_Content", schema: "Purchasing", table: "DraftOrders");
        migrationBuilder.AddCheckConstraint(name: "CK_DraftOrders_Content", schema: "Purchasing", table: "DraftOrders",
            sql: "[ContentSchemaVersion] IN (1,2,3,4) AND ISJSON([ContentJson],OBJECT)=1 AND DATALENGTH([ContentJson])<=1048576");
        FinancialDraftOrderSchema.Protect(migrationBuilder);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("THROW 50020, 'Draft financial adjustments require forward correction or guarded recovery.', 1;");
}
