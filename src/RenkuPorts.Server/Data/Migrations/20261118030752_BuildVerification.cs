using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RenkuPorts.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class BuildVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SelfVerified",
                table: "Packages",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Approval",
                table: "Jobs",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovalReason",
                table: "Jobs",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedAt",
                table: "Jobs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ApprovedBuildId",
                table: "Jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedBy",
                table: "Jobs",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SelfVerified",
                table: "Jobs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "DateEmbeddingPorts",
                columns: table => new
                {
                    PortName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SetBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SetAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DateEmbeddingPorts", x => x.PortName);
                });

            migrationBuilder.CreateTable(
                name: "JobBuilds",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobId = table.Column<long>(type: "bigint", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    ClientId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClientName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OwnerId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExclusiveUntil = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Digest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DiffJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobBuilds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobBuilds_BuildClients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "BuildClients",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_JobBuilds_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "StagedPackages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BuildId = table.Column<long>(type: "bigint", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    BlobName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BuildDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Extra = table.Column<bool>(type: "bit", nullable: false),
                    Digest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ListingJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StagedPackages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StagedPackages_JobBuilds_BuildId",
                        column: x => x.BuildId,
                        principalTable: "JobBuilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobBuilds_ClientId",
                table: "JobBuilds",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_JobBuilds_JobId_Number",
                table: "JobBuilds",
                columns: new[] { "JobId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StagedPackages_BlobName",
                table: "StagedPackages",
                column: "BlobName");

            migrationBuilder.CreateIndex(
                name: "IX_StagedPackages_BuildId_FileName",
                table: "StagedPackages",
                columns: new[] { "BuildId", "FileName" },
                unique: true);

            // Builds in flight have no JobBuild row: give their slot back (the client's next upload gets 409 and it fails the job;
            // nothing is lost but that build). Unpublished uploads move to staging only through new builds, so drop their rows;
            // their content-addressed blobs stay (no blob access here).
            migrationBuilder.Sql("""
                UPDATE Jobs SET Status = 'Queued', ClaimedByClientId = NULL, ClaimedAt = NULL, LeaseExpiresAt = NULL WHERE Status = 'InProgress';
                DELETE p FROM Packages p JOIN Jobs j ON j.Id = p.JobId WHERE p.ImportId IS NULL AND j.Status <> 'Succeeded';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DateEmbeddingPorts");

            migrationBuilder.DropTable(
                name: "StagedPackages");

            migrationBuilder.DropTable(
                name: "JobBuilds");

            migrationBuilder.DropColumn(
                name: "SelfVerified",
                table: "Packages");

            migrationBuilder.DropColumn(
                name: "Approval",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ApprovalReason",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ApprovedBuildId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ApprovedBy",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "SelfVerified",
                table: "Jobs");
        }
    }
}
