using System.Net;
using NeonLedger.Api;
using NeonLedger.Domain;
using NeonLedger.Tests.Infrastructure;
using static NeonLedger.Tests.Infrastructure.LedgerApp;

namespace NeonLedger.Tests;

public sealed class EntryListingTests(LedgerApp app) : IClassFixture<LedgerApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Entries_are_listed_newest_first_and_page_without_gaps_or_repeats()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 10_000);
        var payee = await app.CreateAccountAsync("Payee");
        for (var amount = 1; amount <= 5; amount++)
        {
            await app.PostOkAsync($"Payment {amount}", Debit(payer, amount), Credit(payee, amount));
        }

        var first = await GetPageAsync($"/v1/accounts/{payee.Id}/entries?limit=2");
        var second = await GetPageAsync($"/v1/accounts/{payee.Id}/entries?limit=2&before={first.NextBefore}");
        var third = await GetPageAsync($"/v1/accounts/{payee.Id}/entries?limit=2&before={second.NextBefore}");

        Assert.Equal([5, 4], first.Items.Select(entry => entry.Amount));
        Assert.Equal([3, 2], second.Items.Select(entry => entry.Amount));
        Assert.Equal([1], third.Items.Select(entry => entry.Amount));
        Assert.Null(third.NextBefore);

        var newest = first.Items[0];
        Assert.Equal("Payment 5", newest.Description);
        Assert.Equal(Direction.Credit, newest.Direction);
        Assert.Equal(15, newest.BalanceAfter);
    }

    [Fact]
    public async Task A_page_that_ends_exactly_at_the_last_entry_has_no_next_page()
    {
        var account = await app.CreateFundedAccountAsync("Customer", 100);

        var page = await GetPageAsync($"/v1/accounts/{account.Id}/entries?limit=1");

        Assert.Single(page.Items);
        Assert.Null(page.NextBefore);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task A_page_size_outside_the_limits_is_refused(int limit)
    {
        var account = await app.CreateAccountAsync("Customer");

        var response = await app.Client.GetAsync($"/v1/accounts/{account.Id}/entries?limit={limit}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Entries_of_an_unknown_account_are_not_found()
    {
        var response = await app.Client.GetAsync($"/v1/accounts/{Guid.NewGuid()}/entries", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<EntryPage> GetPageAsync(string path) => await ReadAsync<EntryPage>(await app.Client.GetAsync(path, Ct));
}
