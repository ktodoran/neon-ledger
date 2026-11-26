using System.Net;
using NeonLedger.Api;
using NeonLedger.Tests.Infrastructure;
using static NeonLedger.Tests.Infrastructure.LedgerApp;

namespace NeonLedger.Tests;

public sealed class ReversalTests(LedgerApp app) : IClassFixture<LedgerApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_reversal_puts_the_money_back_and_links_to_the_original()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        var original = await app.PostOkAsync("Payment", Debit(payer, 400), Credit(payee, 400));

        var response = await app.ReverseAsync(NewKey(), original.Id);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var reversal = await ReadAsync<TransactionResponse>(response);
        Assert.Equal(original.Id, reversal.ReversesTransactionId);
        Assert.NotEqual(original.Id, reversal.Id);
        Assert.Equal(1_000, (await app.GetAccountAsync(payer.Id)).Balance);
        Assert.Equal(0, (await app.GetAccountAsync(payee.Id)).Balance);

        // The original is still there, unchanged: history is added to, never edited.
        var fetched = await ReadAsync<TransactionResponse>(await app.Client.GetAsync($"/v1/transactions/{original.Id}", Ct));
        Assert.Equivalent(original, fetched, strict: true);
    }

    [Fact]
    public async Task A_transaction_can_be_reversed_only_once()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        var original = await app.PostOkAsync("Payment", Debit(payer, 400), Credit(payee, 400));
        await app.ReverseAsync(NewKey(), original.Id);

        var second = await app.ReverseAsync(NewKey(), original.Id);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("already_reversed", await ReadCodeAsync(second));
        Assert.Equal(1_000, (await app.GetAccountAsync(payer.Id)).Balance);
    }

    [Fact]
    public async Task Ten_simultaneous_reversals_of_one_transaction_post_exactly_one()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        var original = await app.PostOkAsync("Payment", Debit(payer, 400), Credit(payee, 400));

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            Task.Run(() => app.ReverseAsync(NewKey(), original.Id), Ct)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Equal(9, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1_000, (await app.GetAccountAsync(payer.Id)).Balance);
    }

    [Fact]
    public async Task A_reversal_cannot_itself_be_reversed()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        var original = await app.PostOkAsync("Payment", Debit(payer, 400), Credit(payee, 400));
        var reversal = await ReadAsync<TransactionResponse>(await app.ReverseAsync(NewKey(), original.Id));

        var response = await app.ReverseAsync(NewKey(), reversal.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("reversal_not_reversible", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task A_reversal_is_refused_when_the_money_has_already_moved_on()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        var elsewhere = await app.CreateAccountAsync("Elsewhere");
        var original = await app.PostOkAsync("Payment", Debit(payer, 400), Credit(payee, 400));
        await app.PostOkAsync("Spent", Debit(payee, 400), Credit(elsewhere, 400));

        var response = await app.ReverseAsync(NewKey(), original.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("insufficient_funds", await ReadCodeAsync(response));
        Assert.Equal(600, (await app.GetAccountAsync(payer.Id)).Balance);
    }

    [Fact]
    public async Task Reversing_an_unknown_transaction_is_not_found()
    {
        var response = await app.ReverseAsync(NewKey(), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("transaction_not_found", await ReadCodeAsync(response));
    }
}
