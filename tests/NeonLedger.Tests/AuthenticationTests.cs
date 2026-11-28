using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NeonLedger.Access;
using NeonLedger.Api;
using NeonLedger.Domain;
using NeonLedger.Tests.Infrastructure;
using static NeonLedger.Tests.Infrastructure.LedgerApp;

namespace NeonLedger.Tests;

public sealed class AuthenticationTests(LedgerApp app) : IClassFixture<LedgerApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_request_with_no_key_is_refused_and_told_how_to_send_one()
    {
        using var anonymous = app.CreateClient(token: null);

        var response = await anonymous.GetAsync("/v1/accounts", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("unauthenticated", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task A_write_with_no_key_changes_nothing()
    {
        using var anonymous = app.CreateClient(token: null);
        var before = await app.Client.GetFromJsonAsync<AccountPage>("/v1/accounts?limit=200", Json, Ct);

        var response = await anonymous.PostAsJsonAsync(
            "/v1/accounts",
            new CreateAccountRequest("Opened by nobody", "USD", Direction.Credit),
            Json,
            Ct);

        var after = await app.Client.GetFromJsonAsync<AccountPage>("/v1/accounts?limit=200", Json, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(before!.Items.Count, after!.Items.Count);
    }

    [Fact]
    public async Task A_key_that_is_mistyped_unknown_or_wrong_gets_one_answer()
    {
        var real = await app.IssueKeyAsync("mistyped");
        var wrongSecret = real.Token[..^4] + (real.Token.EndsWith("0000", StringComparison.Ordinal) ? "1111" : "0000");
        var unknownId = $"nl_{new string('0', 12)}_{real.Token[16..]}";
        string[] presented = ["not-a-key", real.Token.ToUpperInvariant(), wrongSecret, unknownId, real.Token + "0"];

        var answers = new List<string>();
        foreach (var token in presented)
        {
            using var client = app.CreateClient(token);
            var response = await client.GetAsync("/v1/accounts", Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            answers.Add(await ReadDetailAsync(response));
        }

        // Nothing in the answer says which part was wrong.
        Assert.Single(answers.Distinct());
    }

    [Fact]
    public async Task A_scheme_other_than_bearer_is_not_a_key()
    {
        var key = await app.IssueKeyAsync("basic");
        using var client = app.CreateClient(token: null);
        client.DefaultRequestHeaders.Authorization = new("Basic", key.Token);

        var response = await client.GetAsync("/v1/accounts", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_revoked_key_stops_working_with_the_next_request()
    {
        var key = await app.IssueKeyAsync("leaving");
        using var client = app.CreateClient(key.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/accounts", Ct)).StatusCode);

        await using (var scope = app.CreateScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<KeyIssuer>().RevokeAsync(key.KeyId, Ct));
        }

        var response = await client.GetAsync("/v1/accounts", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthenticated", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task A_read_only_key_reads_everything_and_writes_nothing()
    {
        var payer = await app.CreateFundedAccountAsync("Watched payer", 5_000);
        var payee = await app.CreateAccountAsync("Watched payee");
        var payment = await app.CreatePaymentOkAsync(Payments.PaymentRail.Ach, payer, payee, 1_000);
        var key = await app.IssueKeyAsync("auditor", AccessLevel.Read);
        using var auditor = app.CreateClient(key.Token);

        string[] reads =
        [
            "/v1/accounts",
            $"/v1/accounts/{payer.Id}",
            $"/v1/accounts/{payer.Id}/entries",
            $"/v1/payments/{payment.Id}",
            "/v1/events",
            "/v1/integrity",
        ];
        foreach (var path in reads)
        {
            Assert.Equal(HttpStatusCode.OK, (await auditor.GetAsync(path, Ct)).StatusCode);
        }

        var open = await auditor.PostAsJsonAsync("/v1/accounts", new CreateAccountRequest("Mine", "USD", Direction.Credit), Json, Ct);
        using var settle = new HttpRequestMessage(HttpMethod.Post, $"/v1/payments/{payment.Id}/settle");
        settle.Headers.Add(Idempotent.KeyHeader, NewKey());
        var settled = await auditor.SendAsync(settle, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, open.StatusCode);
        Assert.Equal("forbidden", await ReadCodeAsync(open));
        Assert.Equal(HttpStatusCode.Forbidden, settled.StatusCode);
        Assert.Equal(Payments.PaymentStatus.Pending, (await app.GetPaymentAsync(payment.Id)).Status);
    }

    [Fact]
    public async Task The_health_check_needs_no_key()
    {
        using var anonymous = app.CreateClient(token: null);

        var response = await anonymous.GetAsync("/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Two_callers_may_use_the_same_idempotency_key_without_meeting()
    {
        var funding = await app.CreateFundingAccountAsync();
        var first = await app.CreateAccountAsync("First caller's customer");
        var second = await app.CreateAccountAsync("Second caller's customer");
        using var one = app.CreateClient((await app.IssueKeyAsync("billing")).Token);
        using var two = app.CreateClient((await app.IssueKeyAsync("payroll")).Token);
        var key = NewKey();

        var fromOne = await PostAsync(one, key, "Billing deposit", Debit(funding, 700), Credit(first, 700));
        var fromTwo = await PostAsync(two, key, "Payroll deposit", Debit(funding, 900), Credit(second, 900));

        // Neither is a replay of the other, and neither is refused for reusing a key.
        Assert.Equal(HttpStatusCode.Created, fromOne.StatusCode);
        Assert.Equal(HttpStatusCode.Created, fromTwo.StatusCode);
        Assert.False(fromTwo.Headers.Contains(Idempotent.ReplayedHeader));
        Assert.Equal(700, (await app.GetAccountAsync(first.Id)).Balance);
        Assert.Equal(900, (await app.GetAccountAsync(second.Id)).Balance);
    }

    [Fact]
    public async Task A_retry_is_still_a_replay_after_the_caller_changes_its_key()
    {
        var funding = await app.CreateFundingAccountAsync();
        var customer = await app.CreateAccountAsync("Rotating caller's customer");
        var oldKey = await app.IssueKeyAsync("rotating");
        var newKey = await app.IssueKeyAsync("rotating");
        using var before = app.CreateClient(oldKey.Token);
        using var after = app.CreateClient(newKey.Token);
        var key = NewKey();

        var first = await PostAsync(before, key, "Deposit", Debit(funding, 400), Credit(customer, 400));
        var retry = await PostAsync(after, key, "Deposit", Debit(funding, 400), Credit(customer, 400));

        Assert.Equal(oldKey.CallerId, newKey.CallerId);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("true", Assert.Single(retry.Headers.GetValues(Idempotent.ReplayedHeader)));
        Assert.Equal(400, (await app.GetAccountAsync(customer.Id)).Balance);
    }

    [Fact]
    public async Task The_database_holds_a_hash_of_a_key_and_not_the_key()
    {
        var key = await app.IssueKeyAsync("stored");
        var secret = key.Token[16..];

        await using var connection = await app.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT row_to_json(k)::text FROM api_keys k WHERE id = @id";
        command.Parameters.AddWithValue("id", key.KeyId);
        var row = (string)(await command.ExecuteScalarAsync(Ct))!;

        Assert.Equal(64, secret.Length);
        Assert.DoesNotContain(secret, row, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(key.KeyId, row);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string key, string description, params EntryRequest[] entries)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/transactions")
        {
            Content = JsonContent.Create(new PostTransactionRequest(description, entries), options: Json),
        };
        request.Headers.Add(Idempotent.KeyHeader, key);
        return client.SendAsync(request, Ct);
    }

    private static async Task<string> ReadDetailAsync(HttpResponseMessage response)
    {
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.GetProperty("detail").GetString()!;
    }
}
