using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NeonLedger.Data.Migrations
{
    /// <inheritdoc />
    public partial class Payments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_accounts_balance_not_negative",
                table: "accounts");

            migrationBuilder.AddColumn<long>(
                name: "held",
                table: "accounts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rail = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    description = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    settlement_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    return_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    return_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                    table.CheckConstraint("ck_payments_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_payments_distinct_accounts", "source_account_id <> destination_account_id");
                    table.CheckConstraint("ck_payments_instant_is_final", "rail <> 'instant' OR status = 'settled'");
                    table.CheckConstraint("ck_payments_rail", "rail IN ('instant', 'ach')");
                    table.CheckConstraint("ck_payments_return_matches_status", "(return_transaction_id IS NOT NULL) = (status = 'returned') AND (return_reason IS NOT NULL) = (status = 'returned')");
                    table.CheckConstraint("ck_payments_settlement_matches_status", "(settlement_transaction_id IS NOT NULL) = (status IN ('settled', 'returned'))");
                    table.CheckConstraint("ck_payments_status", "status IN ('pending', 'settled', 'canceled', 'returned')");
                    table.ForeignKey(
                        name: "fk_payments_accounts_destination_account_id",
                        column: x => x.destination_account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payments_accounts_source_account_id",
                        column: x => x.source_account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payments_transactions_return_transaction_id",
                        column: x => x.return_transaction_id,
                        principalTable: "transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_payments_transactions_settlement_transaction_id",
                        column: x => x.settlement_transaction_id,
                        principalTable: "transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_accounts_available_not_negative",
                table: "accounts",
                sql: "allow_negative OR balance - held >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_accounts_held_not_negative",
                table: "accounts",
                sql: "held >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_payments_destination_account_id",
                table: "payments",
                column: "destination_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_payments_return_transaction_id",
                table: "payments",
                column: "return_transaction_id");

            migrationBuilder.CreateIndex(
                name: "ix_payments_settlement_transaction_id",
                table: "payments",
                column: "settlement_transaction_id");

            migrationBuilder.CreateIndex(
                name: "ix_payments_source_account_id_status",
                table: "payments",
                columns: new[] { "source_account_id", "status" });

            // A payment's facts are fixed when it is created, and its status can only
            // move forward: pending to settled or canceled, settled to returned.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION ledger_guard_payment_change() RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'payments cannot be deleted'
                            USING ERRCODE = 'restrict_violation';
                    END IF;

                    IF (NEW.id, NEW.rail, NEW.amount, NEW.currency, NEW.description,
                        NEW.source_account_id, NEW.destination_account_id, NEW.created_at)
                       IS DISTINCT FROM
                       (OLD.id, OLD.rail, OLD.amount, OLD.currency, OLD.description,
                        OLD.source_account_id, OLD.destination_account_id, OLD.created_at) THEN
                        RAISE EXCEPTION 'a payment''s rail, amount, currency, description and accounts cannot change'
                            USING ERRCODE = 'restrict_violation';
                    END IF;

                    IF NEW.status <> OLD.status AND NOT (
                           (OLD.status = 'pending' AND NEW.status IN ('settled', 'canceled'))
                        OR (OLD.status = 'settled' AND NEW.status = 'returned')) THEN
                        RAISE EXCEPTION 'a payment cannot go from % to %', OLD.status, NEW.status
                            USING ERRCODE = 'restrict_violation';
                    END IF;

                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER payments_guard
                    BEFORE UPDATE OR DELETE ON payments
                    FOR EACH ROW EXECUTE FUNCTION ledger_guard_payment_change();
                CREATE TRIGGER payments_no_truncate
                    BEFORE TRUNCATE ON payments
                    FOR EACH STATEMENT EXECUTE FUNCTION ledger_reject_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payments");

            migrationBuilder.Sql("DROP FUNCTION ledger_guard_payment_change();");

            migrationBuilder.DropCheckConstraint(
                name: "ck_accounts_available_not_negative",
                table: "accounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_accounts_held_not_negative",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "held",
                table: "accounts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_accounts_balance_not_negative",
                table: "accounts",
                sql: "allow_negative OR balance >= 0");
        }
    }
}
