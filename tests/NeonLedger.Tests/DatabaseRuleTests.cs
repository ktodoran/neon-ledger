using NeonLedger.Payments;
using NeonLedger.Tests.Infrastructure;
using Npgsql;
using static NeonLedger.Tests.Infrastructure.LedgerApp;

namespace NeonLedger.Tests;

/// <summary>
/// These go around the application and write SQL directly. The rules they test
/// are enforced by PostgreSQL, so a bug in this service, a script, or another
/// service sharing the database cannot break them.
/// </summary>
public sealed class DatabaseRuleTests(LedgerApp app) : IClassFixture<LedgerApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_unbalanced_transaction_cannot_be_committed()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        await using var connection = await app.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        var id = Guid.NewGuid();

        await ExecuteAsync(
            connection,
            $"""
            INSERT INTO transactions (id, description, currency, posted_at) VALUES ('{id}', 'Hand-written', 'USD', now());
            INSERT INTO entries (transaction_id, account_id, direction, amount, balance_after) VALUES ('{id}', '{payer.Id}', 'debit', 500, 500);
            INSERT INTO entries (transaction_id, account_id, direction, amount, balance_after) VALUES ('{id}', '{payee.Id}', 'credit', 400, 400);
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync(Ct));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Contains("is not balanced", error.MessageText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("UPDATE entries SET amount = amount + 1")]
    [InlineData("DELETE FROM entries")]
    [InlineData("TRUNCATE entries")]
    [InlineData("UPDATE transactions SET description = 'edited'")]
    [InlineData("DELETE FROM transactions")]
    [InlineData("TRUNCATE transactions CASCADE")]
    [InlineData("UPDATE events SET type = 'edited'")]
    [InlineData("DELETE FROM events")]
    [InlineData("TRUNCATE events")]
    public async Task Posted_history_cannot_be_changed_or_removed(string sql)
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        await app.PostOkAsync("Payment", Debit(payer, 100), Credit(payee, 100));
        await using var connection = await app.OpenConnectionAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, sql));

        Assert.Equal(PostgresErrorCodes.RestrictViolation, error.SqlState);
        Assert.Contains("append-only", error.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_balance_cannot_be_set_below_zero_on_an_account_that_forbids_it()
    {
        var account = await app.CreateFundedAccountAsync("Customer", 1_000);
        await using var connection = await app.OpenConnectionAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connection, $"UPDATE accounts SET balance = -1 WHERE id = '{account.Id}'"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("ck_accounts_available_not_negative", error.ConstraintName);
    }

    [Fact]
    public async Task An_entry_amount_must_be_positive()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        await using var connection = await app.OpenConnectionAsync();
        var id = Guid.NewGuid();

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connection,
            $"""
            INSERT INTO transactions (id, description, currency, posted_at) VALUES ('{id}', 'Hand-written', 'USD', now());
            INSERT INTO entries (transaction_id, account_id, direction, amount, balance_after) VALUES ('{id}', '{payer.Id}', 'debit', -5, 0);
            """));

        Assert.Equal("ck_entries_amount_positive", error.ConstraintName);
    }

    [Theory]
    [InlineData("UPDATE payments SET amount = amount + 1", "cannot change")]
    [InlineData("UPDATE payments SET description = 'edited'", "cannot change")]
    [InlineData("UPDATE payments SET status = 'pending', settlement_transaction_id = NULL WHERE status = 'settled'", "cannot go from settled to pending")]
    [InlineData("UPDATE payments SET status = 'pending' WHERE status = 'canceled'", "cannot go from canceled to pending")]
    [InlineData("DELETE FROM payments", "cannot be deleted")]
    [InlineData("TRUNCATE payments", "append-only")]
    public async Task A_payment_cannot_be_rewritten_or_moved_backward(string sql, string expected)
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        await app.SettleAsync(NewKey(), (await app.CreatePaymentOkAsync(PaymentRail.Ach, payer, payee, 100)).Id);
        await app.CancelAsync(NewKey(), (await app.CreatePaymentOkAsync(PaymentRail.Ach, payer, payee, 100)).Id);
        await using var connection = await app.OpenConnectionAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, sql));

        Assert.Equal(PostgresErrorCodes.RestrictViolation, error.SqlState);
        Assert.Contains(expected, error.MessageText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("UPDATE payments SET status = 'settled' WHERE status = 'pending'", "ck_payments_settlement_matches_status")]
    [InlineData("UPDATE payments SET status = 'canceled', return_reason = 'R01' WHERE status = 'pending'", "ck_payments_return_matches_status")]
    public async Task A_payment_row_cannot_claim_a_status_it_has_no_evidence_for(string sql, string constraint)
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        await app.CreatePaymentOkAsync(PaymentRail.Ach, payer, payee, 100);
        await using var connection = await app.OpenConnectionAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, sql));

        Assert.Equal(constraint, error.ConstraintName);
    }

    [Theory]
    [InlineData("held = -1", "ck_accounts_held_not_negative")]
    [InlineData("held = balance + 1", "ck_accounts_available_not_negative")]
    public async Task A_hold_cannot_be_negative_or_larger_than_the_balance(string assignment, string constraint)
    {
        var account = await app.CreateFundedAccountAsync("Customer", 1_000);
        await using var connection = await app.OpenConnectionAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync(connection, $"UPDATE accounts SET {assignment} WHERE id = '{account.Id}'"));

        Assert.Equal(constraint, error.ConstraintName);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
