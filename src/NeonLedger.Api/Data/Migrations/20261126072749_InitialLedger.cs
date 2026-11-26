using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NeonLedger.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    normal_balance = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    allow_negative = table.Column<bool>(type: "boolean", nullable: false),
                    balance = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                    table.CheckConstraint("ck_accounts_balance_not_negative", "allow_negative OR balance >= 0");
                    table.CheckConstraint("ck_accounts_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_accounts_normal_balance", "normal_balance IN ('debit', 'credit')");
                });

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    operation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    request_hash = table.Column<string>(type: "char(64)", nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    response_body = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reverses_transaction_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transactions", x => x.id);
                    table.ForeignKey(
                        name: "fk_transactions_transactions_reverses_transaction_id",
                        column: x => x.reverses_transaction_id,
                        principalTable: "transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    balance_after = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entries", x => x.id);
                    table.CheckConstraint("ck_entries_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_entries_direction", "direction IN ('debit', 'credit')");
                    table.ForeignKey(
                        name: "fk_entries_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_entries_transactions_transaction_id",
                        column: x => x.transaction_id,
                        principalTable: "transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_entries_account_id_id",
                table: "entries",
                columns: new[] { "account_id", "id" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_entries_transaction_id",
                table: "entries",
                column: "transaction_id");

            migrationBuilder.CreateIndex(
                name: "ix_transactions_reverses_transaction_id",
                table: "transactions",
                column: "reverses_transaction_id",
                unique: true);

            // The rules below live in the database so they hold for every writer,
            // not only for this application.

            // 1. A transaction's debits equal its credits. Checked once per entry at
            //    commit, when all of the transaction's entries are in place.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION ledger_assert_transaction_balanced() RETURNS trigger AS $$
                DECLARE
                    difference bigint;
                BEGIN
                    SELECT COALESCE(SUM(CASE direction WHEN 'debit' THEN amount ELSE -amount END), 0)
                    INTO difference
                    FROM entries
                    WHERE transaction_id = NEW.transaction_id;

                    IF difference <> 0 THEN
                        RAISE EXCEPTION 'transaction % is not balanced: debits minus credits is %', NEW.transaction_id, difference
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NULL;
                END;
                $$ LANGUAGE plpgsql;

                CREATE CONSTRAINT TRIGGER entries_transaction_balanced
                    AFTER INSERT ON entries
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION ledger_assert_transaction_balanced();
                """);

            // 2. Posted history is append-only. Corrections are new transactions.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION ledger_reject_change() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION '% is append-only: % is not allowed', TG_TABLE_NAME, TG_OP
                        USING ERRCODE = 'restrict_violation';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER entries_append_only
                    BEFORE UPDATE OR DELETE ON entries
                    FOR EACH ROW EXECUTE FUNCTION ledger_reject_change();
                CREATE TRIGGER entries_no_truncate
                    BEFORE TRUNCATE ON entries
                    FOR EACH STATEMENT EXECUTE FUNCTION ledger_reject_change();
                CREATE TRIGGER transactions_append_only
                    BEFORE UPDATE OR DELETE ON transactions
                    FOR EACH ROW EXECUTE FUNCTION ledger_reject_change();
                CREATE TRIGGER transactions_no_truncate
                    BEFORE TRUNCATE ON transactions
                    FOR EACH STATEMENT EXECUTE FUNCTION ledger_reject_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entries");

            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropTable(
                name: "transactions");

            migrationBuilder.Sql(
                """
                DROP FUNCTION ledger_assert_transaction_balanced();
                DROP FUNCTION ledger_reject_change();
                """);
        }
    }
}
