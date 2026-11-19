using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RenkuPorts.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class EgressCap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EgressSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    MonthlyCapGb = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EgressSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EgressUsage",
                columns: table => new
                {
                    Month = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    BytesServed = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EgressUsage", x => x.Month);
                });

            migrationBuilder.InsertData(
                table: "EgressSettings",
                columns: new[] { "Id", "MonthlyCapGb" },
                values: new object[] { 1, 800 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EgressSettings");

            migrationBuilder.DropTable(
                name: "EgressUsage");
        }
    }
}
