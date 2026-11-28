using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NeonLedger.Data.Migrations
{
    /// <inheritdoc />
    public partial class CallerAuthentication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "callers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_callers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    id = table.Column<string>(type: "char(12)", nullable: false),
                    caller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    secret_hash = table.Column<string>(type: "char(64)", nullable: false),
                    access = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_keys", x => x.id);
                    table.CheckConstraint("ck_api_keys_access", "access IN ('read', 'write')");
                    table.ForeignKey(
                        name: "fk_api_keys_callers_caller_id",
                        column: x => x.caller_id,
                        principalTable: "callers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_caller_id",
                table: "api_keys",
                column: "caller_id");

            migrationBuilder.CreateIndex(
                name: "ix_callers_name",
                table: "callers",
                column: "name",
                unique: true);

            // Idempotency keys become scoped to a caller. The ones already stored were used
            // before callers existed. They are kept, under a caller that has no API key and so
            // cannot sign in: the record of what those requests did stays, and their key text
            // is free for a real caller to use.
            migrationBuilder.AddColumn<Guid>(
                name: "caller_id",
                table: "idempotency_keys",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                """
                INSERT INTO callers (id, name, created_at)
                SELECT '00000000-0000-7000-8000-000000000001', 'before-authentication', now()
                WHERE EXISTS (SELECT 1 FROM idempotency_keys);

                UPDATE idempotency_keys SET caller_id = '00000000-0000-7000-8000-000000000001';
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "caller_id",
                table: "idempotency_keys",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.DropPrimaryKey(
                name: "pk_idempotency_keys",
                table: "idempotency_keys");

            migrationBuilder.AddPrimaryKey(
                name: "pk_idempotency_keys",
                table: "idempotency_keys",
                columns: new[] { "caller_id", "key" });

            migrationBuilder.AddForeignKey(
                name: "fk_idempotency_keys_callers_caller_id",
                table: "idempotency_keys",
                column: "caller_id",
                principalTable: "callers",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_idempotency_keys_callers_caller_id",
                table: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "callers");

            migrationBuilder.DropPrimaryKey(
                name: "pk_idempotency_keys",
                table: "idempotency_keys");

            migrationBuilder.DropColumn(
                name: "caller_id",
                table: "idempotency_keys");

            migrationBuilder.AddPrimaryKey(
                name: "pk_idempotency_keys",
                table: "idempotency_keys",
                column: "key");
        }
    }
}
