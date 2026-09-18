// Copyright (c) 2026 The White Stag Collection.
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Workbench.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPurchaseOrderCommitment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "OrderDate",
                schema: "Purchasing",
                table: "DraftOrders",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                schema: "Purchasing",
                table: "DraftOrders",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "State",
                schema: "Purchasing",
                table: "DraftOrders",
                type: "varchar(7)",
                unicode: false,
                maxLength: 7,
                nullable: false,
                defaultValue: "Draft");
            PurchaseOrderSchema.Create(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("THROW 50020, 'Purchase history requires forward correction or guarded recovery.', 1;");
        }
    }
}
