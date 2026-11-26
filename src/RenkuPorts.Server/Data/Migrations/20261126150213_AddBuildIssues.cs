using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RenkuPorts.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildIssues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "IssueId",
                table: "Jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BuildIssues",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Platform = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Signature = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExampleJobId = table.Column<long>(type: "bigint", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResolvedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BuildIssues", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_IssueId",
                table: "Jobs",
                column: "IssueId");

            migrationBuilder.CreateIndex(
                name: "IX_BuildIssues_Platform_Signature",
                table: "BuildIssues",
                columns: new[] { "Platform", "Signature" });

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_BuildIssues_IssueId",
                table: "Jobs",
                column: "IssueId",
                principalTable: "BuildIssues",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_BuildIssues_IssueId",
                table: "Jobs");

            migrationBuilder.DropTable(
                name: "BuildIssues");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_IssueId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "IssueId",
                table: "Jobs");
        }
    }
}
