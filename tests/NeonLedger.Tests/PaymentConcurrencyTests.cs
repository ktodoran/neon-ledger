using System.Net;
using NeonLedger.Api;
using NeonLedger.Payments;
using NeonLedger.Tests.Infrastructure;
using static NeonLedger.Tests.Infrastructure.LedgerApp;

namespace NeonLedger.Tests;

public sealed class PaymentConcurrencyTests(LedgerApp app) : IClassFixture<LedgerApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Simultaneous_pending_payments_cannot_hold_the_same_money_twice()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");

        var responses = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ =>
            Task.Run(() => app.CreatePaymentAsync(NewKey(), PaymentRail.Ach, payer, payee, 100), Ct)));

        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(20, responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));
        var source = await app.GetAccountAsync(payer.Id);
        Assert.Equal(1_000, source.Balance);
        Assert.Equal(1_000, source.Held);
        Assert.True((await app.CheckIntegrityAsync()).Consistent);
    }

    [Fact]
    public async Task Holds_and_direct_postings_racing_for_one_balance_never_overdraw_it()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");

        var responses = await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            Task.Run(
                () => i % 2 == 0
                    ? app.CreatePaymentAsync(NewKey(), PaymentRail.Ach, payer, payee, 100)
                    : app.PostAsync(NewKey(), "Direct", Debit(payer, 100), Credit(payee, 100)),
                Ct)));

        Assert.Equal(10, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        var source = await app.GetAccountAsync(payer.Id);
        Assert.Equal(0, source.Available);
        Assert.Equal(1_000, source.Balance + (await app.GetAccountAsync(payee.Id)).Balance);
        Assert.True((await app.CheckIntegrityAsync()).Consistent);
    }

    [Fact]
    public async Task When_settle_and_cancel_race_exactly_one_wins()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        var payment = await app.CreatePaymentOkAsync(PaymentRail.Ach, payer, payee, 400);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => i % 2 == 0 ? app.SettleAsync(NewKey(), payment.Id) : app.CancelAsync(NewKey(), payment.Id), Ct)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(19, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var final = await app.GetPaymentAsync(payment.Id);
        var source = await app.GetAccountAsync(payer.Id);
        Assert.Equal(0, source.Held);
        Assert.Equal(final.Status == PaymentStatus.Settled ? 600 : 1_000, source.Balance);
        Assert.Contains(final.Status, new[] { PaymentStatus.Settled, PaymentStatus.Canceled });
        Assert.True((await app.CheckIntegrityAsync()).Consistent);
    }

    [Fact]
    public async Task Ten_simultaneous_returns_send_the_money_back_once()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        var payment = await app.CreatePaymentOkAsync(PaymentRail.Ach, payer, payee, 400);
        await app.SettleAsync(NewKey(), payment.Id);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            Task.Run(() => app.ReturnAsync(NewKey(), payment.Id), Ct)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(9, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1_000, (await app.GetAccountAsync(payer.Id)).Balance);
        Assert.Equal(0, (await app.GetAccountAsync(payee.Id)).Balance);
    }

    [Fact]
    public async Task Many_payments_settling_at_once_each_move_their_own_money()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 5_000);
        var payee = await app.CreateAccountAsync("Payee");
        var payments = new List<PaymentResponse>();
        for (var i = 0; i < 25; i++)
        {
            payments.Add(await app.CreatePaymentOkAsync(PaymentRail.Ach, payer, payee, 200));
        }

        var responses = await Task.WhenAll(payments.Select(p => Task.Run(() => app.SettleAsync(NewKey(), p.Id), Ct)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var source = await app.GetAccountAsync(payer.Id);
        Assert.Equal(0, source.Balance);
        Assert.Equal(0, source.Held);
        Assert.Equal(5_000, (await app.GetAccountAsync(payee.Id)).Balance);
        Assert.True((await app.CheckIntegrityAsync()).Consistent);
    }

    [Fact]
    public async Task A_pending_payment_does_not_wait_on_its_destination()
    {
        // Something else holds the destination's row lock, exactly as a slow settlement would.
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var settlement = await app.CreateFundingAccountAsync();
        await using var connection = await app.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var command = new Npgsql.NpgsqlCommand($"SELECT 1 FROM accounts WHERE id = '{settlement.Id}' FOR NO KEY UPDATE", connection))
        {
            await command.ExecuteScalarAsync(Ct);
        }

        var response = await app.CreatePaymentAsync(NewKey(), PaymentRail.Ach, payer, settlement, 100).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await transaction.RollbackAsync(Ct);
    }
}
