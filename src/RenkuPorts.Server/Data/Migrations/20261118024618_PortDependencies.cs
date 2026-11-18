using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RenkuPorts.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PortDependencies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PortDependencies",
                columns: table => new
                {
                    Platform = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PortName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DependsOn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortDependencies", x => new { x.Platform, x.PortName, x.DependsOn });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PortDependencies");
        }
    }
}
