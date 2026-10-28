using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_classes_room_id",
                table: "classes");

            migrationBuilder.CreateIndex(
                name: "IX_classes_room_id_starts_at_id",
                table: "classes",
                columns: new[] { "room_id", "starts_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_classes_starts_at_id",
                table: "classes",
                columns: new[] { "starts_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_classes_room_id_starts_at_id",
                table: "classes");

            migrationBuilder.DropIndex(
                name: "IX_classes_starts_at_id",
                table: "classes");

            migrationBuilder.CreateIndex(
                name: "IX_classes_room_id",
                table: "classes",
                column: "room_id");
        }
    }
}
