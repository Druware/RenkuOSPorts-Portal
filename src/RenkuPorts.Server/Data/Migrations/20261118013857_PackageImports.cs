using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RenkuPorts.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PackageImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ImportId",
                table: "Packages",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Imports",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SourceUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    SourceHost = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Platform = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DryRun = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedByUserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    CancelRequested = table.Column<bool>(type: "bit", nullable: false),
                    Total = table.Column<int>(type: "int", nullable: false),
                    Missing = table.Column<int>(type: "int", nullable: false),
                    Imported = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Skipped = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Imports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Imports_AspNetUsers_StartedByUserId",
                        column: x => x.StartedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Packages_ImportId",
                table: "Packages",
                column: "ImportId");

            migrationBuilder.CreateIndex(
                name: "IX_Imports_Platform",
                table: "Imports",
                column: "Platform",
                unique: true,
                filter: "[Status] = 'Running'");

            migrationBuilder.CreateIndex(
                name: "IX_Imports_StartedByUserId",
                table: "Imports",
                column: "StartedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Packages_Imports_ImportId",
                table: "Packages",
                column: "ImportId",
                principalTable: "Imports",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Packages_Imports_ImportId",
                table: "Packages");

            migrationBuilder.DropTable(
                name: "Imports");

            migrationBuilder.DropIndex(
                name: "IX_Packages_ImportId",
                table: "Packages");

            migrationBuilder.DropColumn(
                name: "ImportId",
                table: "Packages");
        }
    }
}
