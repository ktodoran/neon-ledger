using NeonLedger.Tests.Infrastructure;
using Npgsql;
using static NeonLedger.Tests.Infrastructure.LedgerApp;

namespace NeonLedger.Tests;

public sealed class IntegrityTests(LedgerApp app) : IClassFixture<LedgerApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_check_passes_after_normal_activity_and_catches_a_balance_that_was_edited_by_hand()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 5_000);
        var payee = await app.CreateAccountAsync("Payee");
        var original = await app.PostOkAsync("Payment", Debit(payer, 1_200), Credit(payee, 1_200));
        await app.PostOkAsync("Payment", Debit(payer, 300), Credit(payee, 300));
        await app.ReverseAsync(NewKey(), original.Id);

        var clean = await app.CheckIntegrityAsync();

        Assert.True(clean.Consistent);
        Assert.Equal(0, clean.DebitsMinusCredits);
        Assert.Equal(3, clean.AccountsChecked);
        Assert.Empty(clean.Mismatches);

        // Stored balances are a cache of the entries. Corrupt one and the check names it.
        await using (var connection = await app.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"UPDATE accounts SET balance = balance + 1 WHERE id = '{payee.Id}'", connection))
        {
            await command.ExecuteNonQueryAsync(Ct);
        }

        var tampered = await app.CheckIntegrityAsync();

        Assert.False(tampered.Consistent);
        var mismatch = Assert.Single(tampered.Mismatches);
        Assert.Equal(payee.Id, mismatch.AccountId);
        Assert.Equal(301, mismatch.StoredBalance);
        Assert.Equal(300, mismatch.BalanceFromEntries);
    }
}
