using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonosControl.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddLogPlaybackIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_PlaybackStats_StartTime_MediaType",
                table: "PlaybackStats",
                columns: new[] { "StartTime", "MediaType" });

            migrationBuilder.CreateIndex(
                name: "IX_Logs_PerformedBy_Timestamp",
                table: "Logs",
                columns: new[] { "PerformedBy", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Logs_Timestamp",
                table: "Logs",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlaybackStats_StartTime_MediaType",
                table: "PlaybackStats");

            migrationBuilder.DropIndex(
                name: "IX_Logs_PerformedBy_Timestamp",
                table: "Logs");

            migrationBuilder.DropIndex(
                name: "IX_Logs_Timestamp",
                table: "Logs");
        }
    }
}
