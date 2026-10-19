using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonosControl.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddManualPlaybackDeadlines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ManualPlaybackDeadlines",
                columns: table => new
                {
                    SpeakerIp = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StopAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RuleKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Generation = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualPlaybackDeadlines", x => x.SpeakerIp);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManualPlaybackDeadlines_StopAtUtc",
                table: "ManualPlaybackDeadlines",
                column: "StopAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManualPlaybackDeadlines");
        }
    }
}
