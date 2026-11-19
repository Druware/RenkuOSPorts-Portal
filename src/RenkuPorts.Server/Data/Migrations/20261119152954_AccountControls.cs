using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RenkuPorts.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Disabled",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Disabled",
                table: "AspNetUsers");
        }
    }
}
