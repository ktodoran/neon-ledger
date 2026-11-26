using System.Net;
using NeonLedger.Tests.Infrastructure;
using static NeonLedger.Tests.Infrastructure.LedgerApp;

namespace NeonLedger.Tests;

public sealed class ConcurrencyTests(LedgerApp app) : IClassFixture<LedgerApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Simultaneous_payments_cannot_spend_the_same_money_twice()
    {
        // 1,000 in the account and fifty payments of 100 racing for it: ten can succeed.
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payees = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => app.CreateAccountAsync($"Payee {i}")));

        var responses = await Task.WhenAll(payees.Select(payee =>
            Task.Run(() => app.PostAsync(NewKey(), "Race", Debit(payer, 100), Credit(payee, 100)), Ct)));

        Assert.Equal(10, responses.Count(response => response.StatusCode == HttpStatusCode.Created));
        var refused = responses.Where(response => response.StatusCode != HttpStatusCode.Created).ToList();
        Assert.Equal(40, refused.Count);
        foreach (var response in refused)
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("insufficient_funds", await ReadCodeAsync(response));
        }

        Assert.Equal(0, (await app.GetAccountAsync(payer.Id)).Balance);
        var paid = await Task.WhenAll(payees.Select(payee => app.GetAccountAsync(payee.Id)));
        Assert.Equal(1_000, paid.Sum(account => account.Balance));
        Assert.True((await app.CheckIntegrityAsync()).Consistent);
    }

    [Fact]
    public async Task Transfers_in_opposite_directions_between_two_accounts_do_not_deadlock()
    {
        var left = await app.CreateFundedAccountAsync("Left", 100_000);
        var right = await app.CreateFundedAccountAsync("Right", 100_000);

        // Without a fixed lock order, left-to-right and right-to-left would each hold
        // one account and wait for the other.
        var responses = await Task.WhenAll(Enumerable.Range(0, 100).Select(i =>
            Task.Run(
                () => i % 2 == 0
                    ? app.PostAsync(NewKey(), "Left to right", Debit(left, 10), Credit(right, 10))
                    : app.PostAsync(NewKey(), "Right to left", Debit(right, 7), Credit(left, 7)),
                Ct)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        Assert.Equal(100_000 - (50 * 10) + (50 * 7), (await app.GetAccountAsync(left.Id)).Balance);
        Assert.Equal(100_000 + (50 * 10) - (50 * 7), (await app.GetAccountAsync(right.Id)).Balance);
        Assert.True((await app.CheckIntegrityAsync()).Consistent);
    }

    [Fact]
    public async Task The_balance_after_each_entry_forms_an_unbroken_chain_under_load()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 50_000);
        var payee = await app.CreateAccountAsync("Payee");

        await Task.WhenAll(Enumerable.Range(1, 40).Select(i =>
            Task.Run(() => app.PostAsync(NewKey(), "Chain", Debit(payer, i), Credit(payee, i)), Ct)));

        // Oldest first: each entry's balance is the previous one plus its own amount.
        var page = await app.Client.GetAsync($"/v1/accounts/{payee.Id}/entries?limit=200", Ct);
        var entries = (await ReadAsync<NeonLedger.Api.EntryPage>(page)).Items.Reverse().ToList();
        Assert.Equal(40, entries.Count);
        long running = 0;
        foreach (var entry in entries)
        {
            running += entry.Amount;
            Assert.Equal(running, entry.BalanceAfter);
        }

        Assert.Equal(running, (await app.GetAccountAsync(payee.Id)).Balance);
    }
}
