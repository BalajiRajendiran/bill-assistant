using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BillAssistant.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Bills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    UploadedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PageCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ChunkCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MetadataStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    MetadataNotes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Utility = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ProviderName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AccountNumber = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    PeriodStart = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    PeriodEnd = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    AmountDueMinor = table.Column<long>(type: "INTEGER", nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: true),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    UsageQuantity = table.Column<double>(type: "REAL", nullable: true),
                    UsageUnit = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bills", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Bills_PeriodEnd",
                table: "Bills",
                column: "PeriodEnd");

            migrationBuilder.CreateIndex(
                name: "IX_Bills_UploadedAt",
                table: "Bills",
                column: "UploadedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Bills_Utility",
                table: "Bills",
                column: "Utility");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Bills");
        }
    }
}
