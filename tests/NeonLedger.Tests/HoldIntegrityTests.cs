using NeonLedger.Payments;
using NeonLedger.Tests.Infrastructure;
using Npgsql;

namespace NeonLedger.Tests;

/// <summary>Kept apart from <see cref="IntegrityTests"/>: each corrupts its own database on purpose.</summary>
public sealed class HoldIntegrityTests(LedgerApp app) : IClassFixture<LedgerApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_check_catches_a_hold_that_no_pending_payment_accounts_for()
    {
        var payer = await app.CreateFundedAccountAsync("Holder", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        await app.CreatePaymentOkAsync(PaymentRail.Ach, payer, payee, 250);
        Assert.Empty((await app.CheckIntegrityAsync()).HoldMismatches);

        await using (var connection = await app.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"UPDATE accounts SET held = held + 50 WHERE id = '{payer.Id}'", connection))
        {
            await command.ExecuteNonQueryAsync(Ct);
        }

        var report = await app.CheckIntegrityAsync();

        Assert.False(report.Consistent);
        var mismatch = Assert.Single(report.HoldMismatches);
        Assert.Equal(payer.Id, mismatch.AccountId);
        Assert.Equal(300, mismatch.StoredHeld);
        Assert.Equal(250, mismatch.HeldFromPayments);
    }
}
