using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonosControl.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddFlyDjDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FlyDjDecisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DecidedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StationName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    StationUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    SpeakerIpsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CandidatesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    BrainWeight = table.Column<double>(type: "REAL", nullable: false),
                    PriorWeight = table.Column<double>(type: "REAL", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DwellSeconds = table.Column<double>(type: "REAL", nullable: true),
                    OverriddenByUser = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlyDjDecisions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FlyDjDecisions_DecidedAtUtc",
                table: "FlyDjDecisions",
                column: "DecidedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FlyDjDecisions");
        }
    }
}
