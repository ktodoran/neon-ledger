using System.Net;
using System.Net.Http.Json;
using NeonLedger.Api;
using NeonLedger.Tests.Infrastructure;

namespace NeonLedger.Tests;

public sealed class AccountListingTests(LedgerApp app) : IClassFixture<LedgerApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Accounts_are_listed_oldest_first_and_page_without_gaps_or_repeats()
    {
        var opened = new List<AccountResponse>();
        foreach (var name in new[] { "First", "Second", "Third", "Fourth", "Fifth" })
        {
            opened.Add(await app.CreateAccountAsync(name));
        }

        // Other tests in this class open accounts in the same database, before or after
        // these, so the test reads every page and looks for its own accounts among them.
        var pages = new List<AccountPage>();
        Guid? after = null;
        do
        {
            pages.Add(await GetPageAsync($"/v1/accounts?limit=2&after={after}"));
            after = pages[^1].NextAfter;
        }
        while (after is not null);

        var listed = pages.SelectMany(page => page.Items).ToList();
        Assert.All(pages.SkipLast(1), page => Assert.Equal(2, page.Items.Count));
        Assert.Equal(listed.Count, listed.Select(account => account.Id).Distinct().Count());
        Assert.All(opened, account => Assert.Contains(account, listed));
        // Ids carry the opening time to the millisecond, so two accounts opened within
        // one millisecond may come back in either order. Nothing older follows anything newer.
        var openedAt = listed.Select(account => account.CreatedAt.ToUnixTimeMilliseconds()).ToList();
        Assert.Equal(openedAt.Order(), openedAt);
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
