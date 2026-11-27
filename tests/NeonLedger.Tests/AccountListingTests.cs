using System.Net;
using System.Net.Http.Json;
using NeonLedger.Api;
using NeonLedger.Tests.Infrastructure;

namespace NeonLedger.Tests;

public sealed class AccountListingTests(LedgerApp app) : IClassFixture<LedgerApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Accounts_are_listed_in_the_order_they_were_opened_and_page_without_gaps_or_repeats()
    {
        var names = new[] { "First", "Second", "Third", "Fourth", "Fifth" };
        var opened = new List<AccountResponse>();
        foreach (var name in names)
        {
            opened.Add(await app.CreateAccountAsync(name));
        }

        var first = await GetPageAsync("/v1/accounts?limit=2");
        var second = await GetPageAsync($"/v1/accounts?limit=2&after={first.NextAfter}");
        var third = await GetPageAsync($"/v1/accounts?limit=2&after={second.NextAfter}");

        Assert.Equal(["First", "Second"], first.Items.Select(account => account.Name));
        Assert.Equal(["Third", "Fourth"], second.Items.Select(account => account.Name));
        Assert.Equal(["Fifth"], third.Items.Select(account => account.Name));
        Assert.Null(third.NextAfter);
        Assert.Equal(opened[0], first.Items[0]);
    }

    [Fact]
    public async Task A_listed_account_carries_its_balance_and_its_hold()
    {
        var payer = await app.CreateFundedAccountAsync("Payer with a hold", 25_000);
        var payee = await app.CreateAccountAsync("Payee of a hold");
        await app.CreatePaymentOkAsync(Payments.PaymentRail.Ach, payer, payee, 12_000);

        var listed = (await GetAllAsync()).Single(account => account.Id == payer.Id);

        Assert.Equal(25_000, listed.Balance);
        Assert.Equal(12_000, listed.Held);
        Assert.Equal(13_000, listed.Available);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task A_page_size_out_of_range_is_refused(int limit)
    {
        var response = await app.Client.GetAsync($"/v1/accounts?limit={limit}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await LedgerApp.ReadCodeAsync(response));
    }

    [Fact]
    public async Task A_cursor_that_is_not_an_account_id_is_refused()
    {
        var response = await app.Client.GetAsync("/v1/accounts?after=somewhere", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await LedgerApp.ReadCodeAsync(response));
    }

    private async Task<List<AccountResponse>> GetAllAsync()
    {
        var accounts = new List<AccountResponse>();
        Guid? after = null;
        do
        {
            var page = await GetPageAsync($"/v1/accounts?limit=200&after={after}");
            accounts.AddRange(page.Items);
            after = page.NextAfter;
        }
        while (after is not null);

        return accounts;
    }

    private async Task<AccountPage> GetPageAsync(string path) =>
        (await app.Client.GetFromJsonAsync<AccountPage>(path, LedgerApp.Json, Ct))!;
}
