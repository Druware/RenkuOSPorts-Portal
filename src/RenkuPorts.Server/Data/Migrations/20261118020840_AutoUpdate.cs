using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RenkuPorts.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AutoUpdate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "QueuedByUserId",
                table: "Jobs",
                type: "nvarchar(450)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.CreateTable(
                name: "AutoUpdateSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    IntervalHours = table.Column<int>(type: "int", nullable: false),
                    Platforms = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RunningSince = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastCommit = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LastResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutoUpdateSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "AutoUpdateSettings",
                columns: new[] { "Id", "Enabled", "IntervalHours", "LastCommit", "LastResultJson", "LastRunAt", "Platforms", "RunningSince" },
                values: new object[] { 1, false, 6, null, null, null, "arm64", null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutoUpdateSettings");

            migrationBuilder.AlterColumn<string>(
                name: "QueuedByUserId",
                table: "Jobs",
                type: "nvarchar(450)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldNullable: true);
        }
    }
}
