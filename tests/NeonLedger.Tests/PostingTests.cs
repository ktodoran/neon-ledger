using System.Net;
using NeonLedger.Api;
using NeonLedger.Domain;
using NeonLedger.Tests.Infrastructure;
using static NeonLedger.Tests.Infrastructure.LedgerApp;

namespace NeonLedger.Tests;

public sealed class PostingTests(LedgerApp app) : IClassFixture<LedgerApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_transfer_moves_money_and_records_the_balance_after_each_entry()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 10_000);
        var payee = await app.CreateAccountAsync("Payee");

        var response = await app.PostAsync(NewKey(), "Rent", Debit(payer, 2_500), Credit(payee, 2_500));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var transaction = await ReadAsync<TransactionResponse>(response);
        Assert.Equal($"/v1/transactions/{transaction.Id}", response.Headers.Location!.ToString());
        Assert.Equal("USD", transaction.Currency);
        Assert.Null(transaction.ReversesTransactionId);
        Assert.Collection(
            transaction.Entries,
            entry =>
            {
                Assert.Equal(payer.Id, entry.AccountId);
                Assert.Equal(Direction.Debit, entry.Direction);
                Assert.Equal(7_500, entry.BalanceAfter);
            },
            entry =>
            {
                Assert.Equal(payee.Id, entry.AccountId);
                Assert.Equal(Direction.Credit, entry.Direction);
                Assert.Equal(2_500, entry.BalanceAfter);
            });

        Assert.Equal(7_500, (await app.GetAccountAsync(payer.Id)).Balance);
        Assert.Equal(2_500, (await app.GetAccountAsync(payee.Id)).Balance);

        var fetched = await app.Client.GetAsync($"/v1/transactions/{transaction.Id}", Ct);
        Assert.Equivalent(transaction, await ReadAsync<TransactionResponse>(fetched), strict: true);
    }

    [Fact]
    public async Task One_transaction_can_split_a_payment_across_several_accounts()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 10_000);
        var biller = await app.CreateAccountAsync("Biller");
        var fees = await app.CreateAccountAsync("Fee revenue");

        await app.PostOkAsync("Bill payment with fee", Debit(payer, 5_150), Credit(biller, 5_000), Credit(fees, 150));

        Assert.Equal(4_850, (await app.GetAccountAsync(payer.Id)).Balance);
        Assert.Equal(5_000, (await app.GetAccountAsync(biller.Id)).Balance);
        Assert.Equal(150, (await app.GetAccountAsync(fees.Id)).Balance);
    }

    [Fact]
    public async Task A_debit_normal_account_grows_on_debits()
    {
        var settlement = await app.CreateFundingAccountAsync();
        var customer = await app.CreateAccountAsync("Customer");

        await app.PostOkAsync("Incoming deposit", Debit(settlement, 900), Credit(customer, 900));

        Assert.Equal(900, (await app.GetAccountAsync(settlement.Id)).Balance);
        Assert.Equal(900, (await app.GetAccountAsync(customer.Id)).Balance);
    }

    [Fact]
    public async Task Debits_must_equal_credits()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");

        var response = await app.PostAsync(NewKey(), "Lopsided", Debit(payer, 500), Credit(payee, 499));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await ReadCodeAsync(response));
        Assert.Equal(1_000, (await app.GetAccountAsync(payer.Id)).Balance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(LedgerService_MaxAmountPlusOne)]
    public async Task An_amount_outside_the_allowed_range_is_refused(long amount)
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");

        var response = await app.PostAsync(NewKey(), "Bad amount", Debit(payer, amount), Credit(payee, amount));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_single_entry_is_not_a_transaction()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);

        var response = await app.PostAsync(NewKey(), "One-sided", Debit(payer, 100));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("""{"description":"Null entry","entries":[null,null]}""")]
    [InlineData("""{"description":"No direction","entries":[{"accountId":"7f1f4c2e-58d1-4f6e-9a3b-1c2d3e4f5a6b","amount":5},{"accountId":"7f1f4c2e-58d1-4f6e-9a3b-1c2d3e4f5a6b","amount":5}]}""")]
    [InlineData("""{"description":"No entries"}""")]
    [InlineData("null")]
    [InlineData("{not json")]
    public async Task A_malformed_body_is_a_bad_request_not_a_server_error(string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/transactions")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(Idempotent.KeyHeader, NewKey());

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task An_entry_for_an_unknown_account_is_refused()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var ghost = new EntryRequest(Guid.NewGuid(), Direction.Credit, 100);

        var response = await app.PostAsync(NewKey(), "To nowhere", Debit(payer, 100), ghost);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("account_not_found", await ReadCodeAsync(response));
        Assert.Equal(1_000, (await app.GetAccountAsync(payer.Id)).Balance);
    }

    [Fact]
    public async Task Accounts_in_different_currencies_cannot_share_a_transaction()
    {
        var dollars = await app.CreateFundedAccountAsync("Dollars", 1_000);
        var euros = await app.CreateAccountAsync("Euros", currency: "EUR");

        var response = await app.PostAsync(NewKey(), "Cross-currency", Debit(dollars, 100), Credit(euros, 100));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("currency_mismatch", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task An_account_that_may_not_go_negative_cannot_be_overdrawn()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");

        var response = await app.PostAsync(NewKey(), "Too much", Debit(payer, 1_001), Credit(payee, 1_001));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("insufficient_funds", await ReadCodeAsync(response));
        Assert.Equal(1_000, (await app.GetAccountAsync(payer.Id)).Balance);
        Assert.Equal(0, (await app.GetAccountAsync(payee.Id)).Balance);
    }

    [Fact]
    public async Task The_whole_balance_can_be_spent()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");

        await app.PostOkAsync("Everything", Debit(payer, 1_000), Credit(payee, 1_000));

        Assert.Equal(0, (await app.GetAccountAsync(payer.Id)).Balance);
    }

    [Fact]
    public async Task Overdraft_is_judged_on_where_the_transaction_ends_not_on_each_entry()
    {
        var hub = await app.CreateFundedAccountAsync("Hub", 100);
        var source = await app.CreateFundedAccountAsync("Source", 500);
        var destination = await app.CreateAccountAsync("Destination");

        // The hub pays out 400 before it receives 500 in the same transaction.
        await app.PostOkAsync(
            "Pass-through",
            Debit(hub, 400),
            Credit(destination, 400),
            Debit(source, 500),
            Credit(hub, 500));

        Assert.Equal(200, (await app.GetAccountAsync(hub.Id)).Balance);
    }

    [Fact]
    public async Task An_unknown_transaction_is_not_found()
    {
        var response = await app.Client.GetAsync($"/v1/transactions/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("transaction_not_found", await ReadCodeAsync(response));
    }

    private const long LedgerService_MaxAmountPlusOne = NeonLedger.Ledger.LedgerService.MaxAmount + 1;
}
