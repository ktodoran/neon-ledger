using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NeonLedger.Data.Migrations
{
    /// <inheritdoc />
    public partial class EventFeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    txid = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "(pg_current_xact_id()::text::bigint)"),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_events_txid_id",
                table: "events",
                columns: new[] { "txid", "id" });

            // Events are history too: the same append-only rule as entries and transactions.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER events_append_only
                    BEFORE UPDATE OR DELETE ON events
                    FOR EACH ROW EXECUTE FUNCTION ledger_reject_change();
                CREATE TRIGGER events_no_truncate
                    BEFORE TRUNCATE ON events
                    FOR EACH STATEMENT EXECUTE FUNCTION ledger_reject_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "events");
        }
    }
}
