using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NeonLedger.Api;
using NeonLedger.Tests.Infrastructure;
using Npgsql;
using static NeonLedger.Tests.Infrastructure.LedgerApp;

namespace NeonLedger.Tests;

public sealed class EventFeedTests(LedgerApp app) : IClassFixture<LedgerApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_posted_transaction_appears_in_the_feed_as_the_api_would_return_it()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");

        var transaction = await app.PostOkAsync("Payment", Debit(payer, 250), Credit(payee, 250));

        var events = await app.ReadEventsUntilAsync(all => all.Any(e => IdOf(e) == transaction.Id));
        var posted = events.Single(e => IdOf(e) == transaction.Id);
        Assert.Equal("transaction.posted", posted.Type);
        Assert.Equal(transaction.PostedAt, posted.OccurredAt, TimeSpan.FromSeconds(5));
        var data = posted.Data.Deserialize<TransactionResponse>(Json)!;
        Assert.Equivalent(transaction, data, strict: true);
    }

    [Fact]
    public async Task A_refused_transaction_leaves_no_event()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 100);
        var payee = await app.CreateAccountAsync("Payee");

        var refused = await app.PostAsync(NewKey(), "Too much", Debit(payer, 500), Credit(payee, 500));
        var marker = await app.PostOkAsync("Marker", Debit(payer, 1), Credit(payee, 1));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var events = await app.ReadEventsUntilAsync(all => all.Any(e => IdOf(e) == marker.Id));
        Assert.DoesNotContain(
            events,
            e => e.Data.TryGetProperty("description", out var description) && description.GetString() == "Too much");
    }

    [Fact]
    public async Task Events_come_back_in_the_order_they_happened_and_paging_does_not_skip_or_repeat()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 1_000);
        var payee = await app.CreateAccountAsync("Payee");
        var posted = new List<Guid>();
        for (var i = 1; i <= 5; i++)
        {
            posted.Add((await app.PostOkAsync($"Payment {i}", Debit(payer, i), Credit(payee, i))).Id);
        }

        var all = await app.ReadEventsUntilAsync(events => events.Any(e => IdOf(e) == posted[^1]));

        Assert.Equal(posted, all.Select(IdOf).Where(posted.Contains));

        // Walking the feed two at a time yields exactly the same sequence.
        var paged = new List<string>();
        var cursor = string.Empty;
        while (paged.Count < all.Count)
        {
            var page = (await app.Client.GetFromJsonAsync<EventPage>($"/v1/events?limit=2&after={cursor}", Json, Ct))!;
            Assert.InRange(page.Items.Count, 1, 2);
            paged.AddRange(page.Items.Select(e => e.Cursor));
            Assert.Equal(page.Items[^1].Cursor, page.NextCursor);
            cursor = page.NextCursor;
        }

        Assert.Equal(all.Select(e => e.Cursor), paged);
    }

    [Fact]
    public async Task At_the_end_of_the_feed_the_cursor_stays_where_it_is()
    {
        var payer = await app.CreateFundedAccountAsync("Payer", 100);
        var all = await app.ReadEventsUntilAsync(events => events.Count > 0);
        Assert.NotEqual(Guid.Empty, payer.Id);
        var end = all[^1].Cursor;

        // Nothing newer than a cursor far in the future.
        var page = (await app.Client.GetFromJsonAsync<EventPage>("/v1/events?after=9223372036854775806-0", Json, Ct))!;

        Assert.Empty(page.Items);
        Assert.Equal("9223372036854775806-0", page.NextCursor);
        Assert.Matches(@"^\d+-\d+$", end);
    }

    [Fact]
    public async Task An_event_is_held_back_while_an_older_transaction_is_still_open_so_a_reader_cannot_skip_it()
    {
        // A slow writer inserts first, so it holds the lower id, but commits last.
        // A reader that followed ids would see the fast event, move past it, and never
        // see the slow one.
        await using var slow = await app.OpenConnectionAsync();
        await using var slowTransaction = await slow.BeginTransactionAsync(Ct);
        await ExecuteAsync(slow, "INSERT INTO events (type, payload, occurred_at) VALUES ('test.slow', '{}', now())");

        await using (var fast = await app.OpenConnectionAsync())
        {
            await ExecuteAsync(fast, "INSERT INTO events (type, payload, occurred_at) VALUES ('test.fast', '{}', now())");
        }

        var whileOpen = (await app.Client.GetFromJsonAsync<EventPage>("/v1/events?limit=500", Json, Ct))!;
        Assert.DoesNotContain(whileOpen.Items, e => e.Type is "test.fast" or "test.slow");

        await slowTransaction.CommitAsync(Ct);

        // Resuming from where the first read stopped now yields both, slow first.
        var after = new List<EventResponse>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (after.Count(e => e.Type.StartsWith("test.", StringComparison.Ordinal)) < 2)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "The held-back events never appeared.");
            var page = (await app.Client.GetFromJsonAsync<EventPage>($"/v1/events?limit=500&after={whileOpen.NextCursor}", Json, Ct))!;
            after = [.. page.Items];
            await Task.Delay(50, Ct);
        }

        Assert.Equal(["test.slow", "test.fast"], after.Where(e => e.Type.StartsWith("test.", StringComparison.Ordinal)).Select(e => e.Type));
    }

    [Theory]
    [InlineData("after=nonsense")]
    [InlineData("after=12")]
    [InlineData("after=-1-5")]
    [InlineData("limit=0")]
    [InlineData("limit=501")]
    public async Task A_bad_cursor_or_page_size_is_refused(string query)
    {
        var response = await app.Client.GetAsync($"/v1/events?{query}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Guid IdOf(EventResponse e) =>
        e.Data.TryGetProperty("id", out var id) && id.TryGetGuid(out var guid) ? guid : Guid.Empty;

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
