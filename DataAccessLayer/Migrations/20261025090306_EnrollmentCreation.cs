using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class EnrollmentCreation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_passes_user_id",
                table: "passes");

            migrationBuilder.DropIndex(
                name: "IX_enrollments_class_id",
                table: "enrollments");

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false),
                    key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    class_id = table.Column<long>(type: "bigint", nullable: false),
                    seats = table.Column<int>(type: "integer", nullable: false),
                    response_json = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_records", x => x.id);
                    table.ForeignKey(
                        name: "FK_idempotency_records_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_passes_user_id",
                table: "passes",
                column: "user_id",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_passes_remaining_visits",
                table: "passes",
                sql: "remaining_visits >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_enrollments_class_id_status",
                table: "enrollments",
                columns: new[] { "class_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_records_user_id_operation_key_hash",
                table: "idempotency_records",
                columns: new[] { "user_id", "operation", "key_hash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropIndex(
                name: "IX_passes_user_id",
                table: "passes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_passes_remaining_visits",
                table: "passes");

            migrationBuilder.DropIndex(
                name: "IX_enrollments_class_id_status",
                table: "enrollments");

            migrationBuilder.CreateIndex(
                name: "IX_passes_user_id",
                table: "passes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_enrollments_class_id",
                table: "enrollments",
                column: "class_id");
        }
    }
}
