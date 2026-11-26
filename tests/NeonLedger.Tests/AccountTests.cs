using System.Net;
using System.Net.Http.Json;
using NeonLedger.Api;
using NeonLedger.Domain;
using NeonLedger.Tests.Infrastructure;

namespace NeonLedger.Tests;

public sealed class AccountTests(LedgerApp app) : IClassFixture<LedgerApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_account_starts_at_zero_and_can_be_read_back()
    {
        var response = await app.Client.PostAsJsonAsync(
            "/v1/accounts",
            new CreateAccountRequest("  Jordan checking  ", "USD", Direction.Credit),
            LedgerApp.Json,
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await LedgerApp.ReadAsync<AccountResponse>(response);
        Assert.Equal("Jordan checking", created.Name);
        Assert.Equal(0, created.Balance);
        Assert.False(created.AllowNegative);
        Assert.EndsWith($"/v1/accounts/{created.Id}", response.Headers.Location!.ToString());

        Assert.Equal(created, await app.GetAccountAsync(created.Id));
    }

    [Theory]
    [InlineData("""{"currency":"USD","normalBalance":"credit"}""", "name")]
    [InlineData("""{"name":"x","currency":"usd","normalBalance":"credit"}""", "currency")]
    [InlineData("""{"name":"x","currency":"US","normalBalance":"credit"}""", "currency")]
    [InlineData("""{"name":"x","currency":"USD"}""", "normalBalance")]
    public async Task An_invalid_account_is_refused_with_the_field_at_fault(string body, string field)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await app.Client.PostAsync("/v1/accounts", content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await LedgerApp.ReadAsync<Microsoft.AspNetCore.Http.HttpValidationProblemDetails>(response);
        Assert.Contains(field, problem.Errors.Keys);
    }

    [Fact]
    public async Task A_direction_that_is_not_debit_or_credit_is_a_bad_request()
    {
        using var content = new StringContent(
            """{"name":"x","currency":"USD","normalBalance":"sideways"}""",
            System.Text.Encoding.UTF8,
            "application/json");
        var response = await app.Client.PostAsync("/v1/accounts", content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task An_unknown_account_is_not_found()
    {
        var response = await app.Client.GetAsync($"/v1/accounts/{Guid.NewGuid()}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("account_not_found", await LedgerApp.ReadCodeAsync(response));
    }
}
