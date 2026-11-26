using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NeonLedger.Api;
using NeonLedger.Data;
using NeonLedger.Domain;
using Npgsql;

namespace NeonLedger.Tests.Infrastructure;

/// <summary>
/// The real application, in process, on its own freshly migrated database.
/// Used as a class fixture: tests in one class share a database, classes do not.
/// </summary>
public sealed class LedgerApp(PostgresServer server) : IAsyncLifetime
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private WebApplicationFactory<Program>? _factory;
    private string _connectionString = string.Empty;

    public HttpClient Client { get; private set; } = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await server.CreateDatabaseAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Ledger", _connectionString);
        });

        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.MigrateAsync();
        Client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    /// <summary>A service scope from the running application, for tests below the HTTP layer.</summary>
    public AsyncServiceScope CreateScope() => _factory!.Services.CreateAsyncScope();

    /// <summary>A direct connection, for tests that go around the application.</summary>
    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }

    public async Task<AccountResponse> CreateAccountAsync(
        string name,
        Direction normalBalance = Direction.Credit,
        bool allowNegative = false,
        string currency = "USD")
    {
        var response = await Client.PostAsJsonAsync(
            "/v1/accounts",
            new CreateAccountRequest(name, currency, normalBalance, allowNegative),
            Json,
            Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountResponse>(Json, Ct))!;
    }

    /// <summary>A debit-normal account that may go negative: where money enters the ledger from.</summary>
    public Task<AccountResponse> CreateFundingAccountAsync(string currency = "USD") =>
        CreateAccountAsync("Settlement", Direction.Debit, allowNegative: true, currency);

    /// <summary>A customer account holding <paramref name="balance"/>, funded from a new settlement account.</summary>
    public async Task<AccountResponse> CreateFundedAccountAsync(string name, long balance)
    {
        var funding = await CreateFundingAccountAsync();
        var account = await CreateAccountAsync(name);
        var response = await PostAsync(NewKey(), "Opening deposit", Debit(funding, balance), Credit(account, balance));
        response.EnsureSuccessStatusCode();
        return await GetAccountAsync(account.Id);
    }

    public async Task<AccountResponse> GetAccountAsync(Guid id) =>
        (await Client.GetFromJsonAsync<AccountResponse>($"/v1/accounts/{id}", Json, Ct))!;

    public Task<HttpResponseMessage> PostAsync(string? key, string description, params EntryRequest[] entries) =>
        SendAsync("/v1/transactions", key, new PostTransactionRequest(description, entries));

    public Task<HttpResponseMessage> ReverseAsync(string? key, Guid transactionId) =>
        SendAsync($"/v1/transactions/{transactionId}/reversal", key, body: null);

    public async Task<TransactionResponse> PostOkAsync(string description, params EntryRequest[] entries)
    {
        var response = await PostAsync(NewKey(), description, entries);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<TransactionResponse>(response);
    }

    public async Task<IntegrityReport> CheckIntegrityAsync() =>
        (await Client.GetFromJsonAsync<IntegrityReport>("/v1/integrity", Json, Ct))!;

    /// <summary>
    /// Reads the event feed from the start until <paramref name="done"/> is satisfied.
    /// The feed holds events back while an older database transaction is still open
    /// anywhere on the server, including in other test classes, so a test waits for
    /// its events instead of expecting them on the first read.
    /// </summary>
    public async Task<List<EventResponse>> ReadEventsUntilAsync(Func<List<EventResponse>, bool> done)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            var events = new List<EventResponse>();
            var cursor = string.Empty;
            while (true)
            {
                var page = (await Client.GetFromJsonAsync<EventPage>($"/v1/events?limit=500&after={cursor}", Json, Ct))!;
                events.AddRange(page.Items);
                cursor = page.NextCursor;
                if (page.Items.Count < 500)
                {
                    break;
                }
            }

            if (done(events))
            {
                return events;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, "The expected events did not appear in the feed.");
            await Task.Delay(50, Ct);
        }
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json, Ct))!;

    /// <summary>The <c>code</c> field of a problem response.</summary>
    public static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public static string NewKey() => Guid.NewGuid().ToString();

    public static EntryRequest Debit(AccountResponse account, long amount) => new(account.Id, Direction.Debit, amount);

    public static EntryRequest Credit(AccountResponse account, long amount) => new(account.Id, Direction.Credit, amount);

    private async Task<HttpResponseMessage> SendAsync(string path, string? key, object? body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        if (key is not null)
        {
            request.Headers.Add(TransactionEndpoints.IdempotencyKeyHeader, key);
        }

        return await Client.SendAsync(request, Ct);
    }
}
