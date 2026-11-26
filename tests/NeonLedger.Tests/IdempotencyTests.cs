using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NeonLedger.Api;
using NeonLedger.Idempotency;
using NeonLedger.Tests.Infrastructure;
using static NeonLedger.Tests.Infrastructure.LedgerApp;

namespace NeonLedger.Tests;

public sealed class IdempotencyTests(LedgerApp app) : IClassFixture<LedgerApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_posting_without_a_key_is_refused()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");

        var response = await app.PostAsync(key: null, "No key", Debit(payer, 100), Credit(payee, 100));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("idempotency_key_required", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task A_retry_with_the_same_key_returns_the_first_response_and_posts_once()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        var key = NewKey();

        var first = await app.PostAsync(key, "Once", Debit(payer, 300), Credit(payee, 300));
        var retry = await app.PostAsync(key, "Once", Debit(payer, 300), Credit(payee, 300));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.False(first.Headers.Contains(Idempotent.ReplayedHeader));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("true", Assert.Single(retry.Headers.GetValues(Idempotent.ReplayedHeader)));
        Assert.Equal(first.Headers.Location, retry.Headers.Location);
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await retry.Content.ReadAsStringAsync(Ct));
        Assert.Equal(700, (await app.GetAccountAsync(payer.Id)).Balance);
    }

    [Fact]
    public async Task A_key_cannot_be_reused_for_a_different_request()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        var key = NewKey();
        await app.PostAsync(key, "Original", Debit(payer, 300), Credit(payee, 300));

        var different = await app.PostAsync(key, "Original", Debit(payer, 301), Credit(payee, 301));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, different.StatusCode);
        Assert.Equal("idempotency_key_reused", await ReadCodeAsync(different));
        Assert.Equal(700, (await app.GetAccountAsync(payer.Id)).Balance);
    }

    [Fact]
    public async Task A_key_used_for_a_posting_cannot_be_used_for_a_reversal()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        var key = NewKey();
        var posted = await ReadAsync<TransactionResponse>(await app.PostAsync(key, "Original", Debit(payer, 300), Credit(payee, 300)));

        var reversal = await app.ReverseAsync(key, posted.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, reversal.StatusCode);
        Assert.Equal("idempotency_key_reused", await ReadCodeAsync(reversal));
    }

    [Fact]
    public async Task A_refusal_is_remembered_too_so_a_retry_cannot_succeed_later_by_surprise()
    {
        var payer = await app.CreateAccountAsync("Payer");
        var payee = await app.CreateAccountAsync("Payee");
        var funding = await app.CreateFundingAccountAsync();
        var key = NewKey();

        var refused = await app.PostAsync(key, "Too early", Debit(payer, 500), Credit(payee, 500));
        await app.PostOkAsync("Deposit arrives", Debit(funding, 500), Credit(payer, 500));
        var retry = await app.PostAsync(key, "Too early", Debit(payer, 500), Credit(payee, 500));
        var fresh = await app.PostAsync(NewKey(), "Too early", Debit(payer, 500), Credit(payee, 500));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, retry.StatusCode);
        Assert.Equal("insufficient_funds", await ReadCodeAsync(retry));
        Assert.Equal("application/problem+json", retry.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Created, fresh.StatusCode);
    }

    [Fact]
    public async Task A_request_that_fails_validation_does_not_use_up_its_key()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        var key = NewKey();

        var invalid = await app.PostAsync(key, "Fix me", Debit(payer, 100), Credit(payee, 99));
        var corrected = await app.PostAsync(key, "Fix me", Debit(payer, 100), Credit(payee, 100));

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Created, corrected.StatusCode);
    }

    [Fact]
    public async Task Twenty_simultaneous_requests_with_one_key_post_exactly_once()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 10_000);
        var payee = await app.CreateAccountAsync("Payee");
        var key = NewKey();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => app.PostAsync(key, "Double-click", Debit(payer, 100), Credit(payee, 100)), Ct)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync(Ct)));
        Assert.Single(bodies.Distinct());
        Assert.Equal(19, responses.Count(response => response.Headers.Contains(Idempotent.ReplayedHeader)));
        Assert.Equal(9_900, (await app.GetAccountAsync(payer.Id)).Balance);
        Assert.Equal(100, (await app.GetAccountAsync(payee.Id)).Balance);
    }

    [Fact]
    public async Task A_write_that_fails_unexpectedly_leaves_its_key_free_for_a_retry()
    {
        var key = NewKey();

        // The first attempt dies partway through, as it would if the process crashed.
        await using (var scope = app.CreateScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<IdempotentExecutor>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => executor.RunAsync(
                key,
                "test_operation",
                "request",
                _ => throw new InvalidOperationException("crashed"),
                Ct));
        }

        // Nothing was committed, so the retry runs the write for real instead of replaying a failure.
        await using (var scope = app.CreateScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<IdempotentExecutor>();
            var retry = await executor.RunAsync(
                key,
                "test_operation",
                "request",
                _ => Task.FromResult((StatusCodes.Status201Created, (object)new { ok = true })),
                Ct);

            Assert.Equal(IdempotencyOutcome.Completed, retry.Outcome);
            Assert.False(retry.Response!.Replayed);
        }
    }
}
