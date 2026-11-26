using System.Net;
using System.Net.Http.Json;
using NeonLedger.Api;
using NeonLedger.Payments;
using NeonLedger.Tests.Infrastructure;
using static NeonLedger.Tests.Infrastructure.LedgerApp;

namespace NeonLedger.Tests;

public sealed class PaymentTests(LedgerApp app) : IClassFixture<LedgerApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_instant_payment_moves_the_money_at_once_and_is_settled()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");

        var response = await app.CreatePaymentAsync(NewKey(), PaymentRail.Instant, payer, payee, 400, "Dinner");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payment = await ReadAsync<PaymentResponse>(response);
        Assert.Equal($"/v1/payments/{payment.Id}", response.Headers.Location!.ToString());
        Assert.Equal(PaymentStatus.Settled, payment.Status);
        Assert.Equal("USD", payment.Currency);
        Assert.NotNull(payment.SettlementTransactionId);
        Assert.Equal(600, (await app.GetAccountAsync(payer.Id)).Balance);
        Assert.Equal(400, (await app.GetAccountAsync(payee.Id)).Balance);
        Assert.Equivalent(payment, await app.GetPaymentAsync(payment.Id), strict: true);

        var transaction = await app.Client.GetFromJsonAsync<TransactionResponse>($"/v1/transactions/{payment.SettlementTransactionId}", Json, Ct);
        Assert.Equal("Dinner", transaction!.Description);
    }

    [Fact]
    public async Task An_instant_payment_without_the_funds_is_refused_and_moves_nothing()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 100);
        var payee = await app.CreateAccountAsync("Payee");

        var response = await app.CreatePaymentAsync(NewKey(), PaymentRail.Instant, payer, payee, 101);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("insufficient_funds", await ReadCodeAsync(response));
        Assert.Equal(100, (await app.GetAccountAsync(payer.Id)).Balance);
    }

    [Fact]
    public async Task A_pending_payment_holds_the_funds_without_moving_them()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");

        var payment = await app.CreatePaymentOkAsync(PaymentRail.Ach, payer, payee, 400);

        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Null(payment.SettlementTransactionId);
        var source = await app.GetAccountAsync(payer.Id);
        Assert.Equal(1_000, source.Balance);
        Assert.Equal(400, source.Held);
        Assert.Equal(600, source.Available);
        Assert.Equal(0, (await app.GetAccountAsync(payee.Id)).Balance);
    }

    [Fact]
    public async Task Held_funds_cannot_be_spent_by_anything_else()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        await app.CreatePaymentOkAsync(PaymentRail.Ach, payer, payee, 400);

        var posting = await app.PostAsync(NewKey(), "Direct posting", Debit(payer, 601), Credit(payee, 601));
        var instant = await app.CreatePaymentAsync(NewKey(), PaymentRail.Instant, payer, payee, 601);
        var secondHold = await app.CreatePaymentAsync(NewKey(), PaymentRail.Ach, payer, payee, 601);
        var withinAvailable = await app.PostAsync(NewKey(), "Direct posting", Debit(payer, 600), Credit(payee, 600));

        Assert.Equal("insufficient_funds", await ReadCodeAsync(posting));
        Assert.Equal("insufficient_funds", await ReadCodeAsync(instant));
        Assert.Equal("insufficient_funds", await ReadCodeAsync(secondHold));
        Assert.Equal(HttpStatusCode.Created, withinAvailable.StatusCode);
        var source = await app.GetAccountAsync(payer.Id);
        Assert.Equal(400, source.Balance);
        Assert.Equal(0, source.Available);
    }

    [Fact]
    public async Task Settling_moves_the_money_and_lifts_the_hold()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        var pending = await app.CreatePaymentOkAsync(PaymentRail.Ach, payer, payee, 400);

        var response = await app.SettleAsync(NewKey(), pending.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var settled = await ReadAsync<PaymentResponse>(response);
        Assert.Equal(PaymentStatus.Settled, settled.Status);
        Assert.NotNull(settled.SettlementTransactionId);
        Assert.True(settled.UpdatedAt >= pending.UpdatedAt);
        var source = await app.GetAccountAsync(payer.Id);
        Assert.Equal(600, source.Balance);
        Assert.Equal(0, source.Held);
        Assert.Equal(400, (await app.GetAccountAsync(payee.Id)).Balance);
    }

    [Fact]
    public async Task A_payment_can_use_the_whole_balance_and_still_settle()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 500);
        var payee = await app.CreateAccountAsync("Payee");
        var pending = await app.CreatePaymentOkAsync(PaymentRail.Ach, payer, payee, 500);

        var response = await app.SettleAsync(NewKey(), pending.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await app.GetAccountAsync(payer.Id)).Balance);
    }

    [Fact]
    public async Task Canceling_gives_the_held_funds_back_and_moves_nothing()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        var pending = await app.CreatePaymentOkAsync(PaymentRail.Ach, payer, payee, 400);

        var response = await app.CancelAsync(NewKey(), pending.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var canceled = await ReadAsync<PaymentResponse>(response);
        Assert.Equal(PaymentStatus.Canceled, canceled.Status);
        Assert.Null(canceled.SettlementTransactionId);
        var source = await app.GetAccountAsync(payer.Id);
        Assert.Equal(1_000, source.Balance);
        Assert.Equal(0, source.Held);
        Assert.Equal(0, (await app.GetAccountAsync(payee.Id)).Balance);
    }

    [Fact]
    public async Task A_return_reverses_the_settlement_and_records_why()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        var pending = await app.CreatePaymentOkAsync(PaymentRail.Ach, payer, payee, 400);
        var settled = await ReadAsync<PaymentResponse>(await app.SettleAsync(NewKey(), pending.Id));

        var response = await app.ReturnAsync(NewKey(), pending.Id, " R01 ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var returned = await ReadAsync<PaymentResponse>(response);
        Assert.Equal(PaymentStatus.Returned, returned.Status);
        Assert.Equal("R01", returned.ReturnReason);
        Assert.Equal(settled.SettlementTransactionId, returned.SettlementTransactionId);
        Assert.Equal(1_000, (await app.GetAccountAsync(payer.Id)).Balance);
        Assert.Equal(0, (await app.GetAccountAsync(payee.Id)).Balance);

        var reversal = await app.Client.GetFromJsonAsync<TransactionResponse>($"/v1/transactions/{returned.ReturnTransactionId}", Json, Ct);
        Assert.Equal(settled.SettlementTransactionId, reversal!.ReversesTransactionId);
    }

    [Fact]
    public async Task A_return_is_refused_when_the_money_has_been_spent_and_the_payment_stays_settled()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        var elsewhere = await app.CreateAccountAsync("Elsewhere");
        var payment = await app.CreatePaymentOkAsync(PaymentRail.Ach, payer, payee, 400);
        await app.SettleAsync(NewKey(), payment.Id);
        await app.PostOkAsync("Spent", Debit(payee, 400), Credit(elsewhere, 400));

        var response = await app.ReturnAsync(NewKey(), payment.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("insufficient_funds", await ReadCodeAsync(response));
        Assert.Equal(PaymentStatus.Settled, (await app.GetPaymentAsync(payment.Id)).Status);
    }

    [Fact]
    public async Task A_deposit_reaches_the_customer_only_when_it_settles()
    {
        var settlement = await app.CreateFundingAccountAsync();
        var customer = await app.CreateAccountAsync("Customer");

        var deposit = await app.CreatePaymentOkAsync(PaymentRail.Ach, settlement, customer, 2_500);
        var before = await app.GetAccountAsync(customer.Id);
        await app.SettleAsync(NewKey(), deposit.Id);
        var after = await app.GetAccountAsync(customer.Id);

        Assert.Equal(0, before.Available);
        Assert.Equal(2_500, after.Available);
        Assert.Equal(2_500, (await app.GetAccountAsync(settlement.Id)).Balance);
    }

    [Fact]
    public async Task An_instant_payment_is_final()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        var payment = await app.CreatePaymentOkAsync(PaymentRail.Instant, payer, payee, 400);

        var settle = await app.SettleAsync(NewKey(), payment.Id);
        var cancel = await app.CancelAsync(NewKey(), payment.Id);
        var giveBack = await app.ReturnAsync(NewKey(), payment.Id);

        foreach (var response in new[] { settle, cancel, giveBack })
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("invalid_payment_state", await ReadCodeAsync(response));
        }

        Assert.Equal(600, (await app.GetAccountAsync(payer.Id)).Balance);
    }

    [Fact]
    public async Task A_payment_only_moves_forward()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        var canceled = await app.CreatePaymentOkAsync(PaymentRail.Ach, payer, payee, 100);
        var settled = await app.CreatePaymentOkAsync(PaymentRail.Ach, payer, payee, 100);
        var returned = await app.CreatePaymentOkAsync(PaymentRail.Ach, payer, payee, 100);
        var pending = await app.CreatePaymentOkAsync(PaymentRail.Ach, payer, payee, 100);
        await app.CancelAsync(NewKey(), canceled.Id);
        await app.SettleAsync(NewKey(), settled.Id);
        await app.SettleAsync(NewKey(), returned.Id);
        await app.ReturnAsync(NewKey(), returned.Id);

        HttpResponseMessage[] refused =
        [
            await app.SettleAsync(NewKey(), canceled.Id),
            await app.CancelAsync(NewKey(), canceled.Id),
            await app.ReturnAsync(NewKey(), canceled.Id),
            await app.SettleAsync(NewKey(), settled.Id),
            await app.CancelAsync(NewKey(), settled.Id),
            await app.SettleAsync(NewKey(), returned.Id),
            await app.CancelAsync(NewKey(), returned.Id),
            await app.ReturnAsync(NewKey(), returned.Id),
            await app.ReturnAsync(NewKey(), pending.Id),
        ];

        foreach (var response in refused)
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("invalid_payment_state", await ReadCodeAsync(response));
        }

        // Only the pending payment still holds funds; only the settled one moved money.
        var source = await app.GetAccountAsync(payer.Id);
        Assert.Equal(900, source.Balance);
        Assert.Equal(100, source.Held);
        Assert.True((await app.CheckIntegrityAsync()).Consistent);
    }

    [Fact]
    public async Task A_retry_of_each_step_replays_the_first_answer_and_acts_once()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        var createKey = NewKey();
        var settleKey = NewKey();

        var created = await app.CreatePaymentAsync(createKey, PaymentRail.Ach, payer, payee, 400);
        var createdAgain = await app.CreatePaymentAsync(createKey, PaymentRail.Ach, payer, payee, 400);
        var payment = await ReadAsync<PaymentResponse>(created);
        var settled = await app.SettleAsync(settleKey, payment.Id);
        var settledAgain = await app.SettleAsync(settleKey, payment.Id);

        Assert.Equal(await created.Content.ReadAsStringAsync(Ct), await createdAgain.Content.ReadAsStringAsync(Ct));
        Assert.Equal(created.Headers.Location, createdAgain.Headers.Location);
        Assert.True(createdAgain.Headers.Contains(Idempotent.ReplayedHeader));
        Assert.Equal(HttpStatusCode.OK, settledAgain.StatusCode);
        Assert.Equal(await settled.Content.ReadAsStringAsync(Ct), await settledAgain.Content.ReadAsStringAsync(Ct));
        Assert.True(settledAgain.Headers.Contains(Idempotent.ReplayedHeader));
        var source = await app.GetAccountAsync(payer.Id);
        Assert.Equal(600, source.Balance);
        Assert.Equal(0, source.Held);
    }

    [Theory]
    [InlineData(PaymentRail.Instant)]
    [InlineData(PaymentRail.Ach)]
    public async Task A_payment_between_currencies_or_to_an_unknown_account_is_refused(PaymentRail rail)
    {
        var dollars = await app.CreateFundedAccountAsync("Dollars", 1_000);
        var euros = await app.CreateAccountAsync("Euros", currency: "EUR");
        var ghost = dollars with { Id = Guid.NewGuid() };

        var mixed = await app.CreatePaymentAsync(NewKey(), rail, dollars, euros, 100);
        var unknownDestination = await app.CreatePaymentAsync(NewKey(), rail, dollars, ghost, 100);
        var unknownSource = await app.CreatePaymentAsync(NewKey(), rail, ghost, dollars, 100);

        Assert.Equal("currency_mismatch", await ReadCodeAsync(mixed));
        Assert.Equal("account_not_found", await ReadCodeAsync(unknownDestination));
        Assert.Equal("account_not_found", await ReadCodeAsync(unknownSource));
        var source = await app.GetAccountAsync(dollars.Id);
        Assert.Equal(1_000, source.Available);
    }

    [Theory]
    [InlineData("""{"sourceAccountId":"7f1f4c2e-58d1-4f6e-9a3b-1c2d3e4f5a6b","destinationAccountId":"8f1f4c2e-58d1-4f6e-9a3b-1c2d3e4f5a6b","amount":5,"description":"x"}""", "rail")]
    [InlineData("""{"rail":"ach","sourceAccountId":"7f1f4c2e-58d1-4f6e-9a3b-1c2d3e4f5a6b","destinationAccountId":"7f1f4c2e-58d1-4f6e-9a3b-1c2d3e4f5a6b","amount":5,"description":"x"}""", "destinationAccountId")]
    [InlineData("""{"rail":"ach","destinationAccountId":"8f1f4c2e-58d1-4f6e-9a3b-1c2d3e4f5a6b","amount":5,"description":"x"}""", "sourceAccountId")]
    [InlineData("""{"rail":"ach","sourceAccountId":"7f1f4c2e-58d1-4f6e-9a3b-1c2d3e4f5a6b","destinationAccountId":"8f1f4c2e-58d1-4f6e-9a3b-1c2d3e4f5a6b","amount":0,"description":"x"}""", "amount")]
    [InlineData("""{"rail":"ach","sourceAccountId":"7f1f4c2e-58d1-4f6e-9a3b-1c2d3e4f5a6b","destinationAccountId":"8f1f4c2e-58d1-4f6e-9a3b-1c2d3e4f5a6b","amount":5}""", "description")]
    public async Task An_invalid_payment_is_refused_with_the_field_at_fault(string body, string field)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(Idempotent.KeyHeader, NewKey());

        var response = await app.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadAsync<Microsoft.AspNetCore.Http.HttpValidationProblemDetails>(response);
        Assert.Contains(field, problem.Errors.Keys);
    }

    [Fact]
    public async Task Every_payment_write_needs_an_idempotency_key_and_a_return_needs_a_reason()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        var payment = await app.CreatePaymentOkAsync(PaymentRail.Ach, payer, payee, 100);

        HttpResponseMessage[] withoutKey =
        [
            await app.CreatePaymentAsync(key: null, PaymentRail.Ach, payer, payee, 100),
            await app.SettleAsync(key: null, payment.Id),
            await app.CancelAsync(key: null, payment.Id),
            await app.ReturnAsync(key: null, payment.Id),
        ];
        var withoutReason = await app.ReturnAsync(NewKey(), payment.Id, reason: "  ");

        foreach (var response in withoutKey)
        {
            Assert.Equal("idempotency_key_required", await ReadCodeAsync(response));
        }

        Assert.Equal(HttpStatusCode.BadRequest, withoutReason.StatusCode);
        Assert.Equal(PaymentStatus.Pending, (await app.GetPaymentAsync(payment.Id)).Status);
    }

    [Fact]
    public async Task An_unknown_payment_is_not_found()
    {
        var id = Guid.NewGuid();

        HttpResponseMessage[] responses =
        [
            await app.Client.GetAsync($"/v1/payments/{id}", Ct),
            await app.SettleAsync(NewKey(), id),
            await app.CancelAsync(NewKey(), id),
            await app.ReturnAsync(NewKey(), id),
        ];

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("payment_not_found", await ReadCodeAsync(response));
        }
    }

    [Fact]
    public async Task A_payment_tells_its_story_in_the_event_feed()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        var payment = await app.CreatePaymentOkAsync(PaymentRail.Ach, payer, payee, 400);
        var settled = await ReadAsync<PaymentResponse>(await app.SettleAsync(NewKey(), payment.Id));
        var returned = await ReadAsync<PaymentResponse>(await app.ReturnAsync(NewKey(), payment.Id));

        bool About(EventResponse e) =>
            e.Data.TryGetProperty("id", out var id) && id.TryGetGuid(out var guid)
            && (guid == payment.Id || guid == settled.SettlementTransactionId || guid == returned.ReturnTransactionId);
        var events = await app.ReadEventsUntilAsync(all => all.Any(e => e.Type == "payment.returned" && About(e)));

        Assert.Equal(
            ["payment.created", "transaction.posted", "payment.settled", "transaction.posted", "payment.returned"],
            events.Where(About).Select(e => e.Type));
        var last = events.Last(About).Data;
        Assert.Equal("returned", last.GetProperty("status").GetString());
        Assert.Equal("R01", last.GetProperty("returnReason").GetString());
    }
}
