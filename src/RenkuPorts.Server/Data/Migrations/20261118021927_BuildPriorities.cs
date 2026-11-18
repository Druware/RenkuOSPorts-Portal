using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RenkuPorts.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class BuildPriorities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PortPriorities",
                columns: table => new
                {
                    Platform = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PortName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RecipeId = table.Column<int>(type: "int", nullable: false),
                    Outstanding = table.Column<bool>(type: "bit", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Dependents = table.Column<int>(type: "int", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortPriorities", x => new { x.Platform, x.PortName });
                    table.ForeignKey(
                        name: "FK_PortPriorities_Recipes_RecipeId",
                        column: x => x.RecipeId,
                        principalTable: "Recipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PortPriorities_Platform_Priority",
                table: "PortPriorities",
                columns: new[] { "Platform", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_PortPriorities_RecipeId",
                table: "PortPriorities",
                column: "RecipeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PortPriorities");
        }
    }
}
