using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using NeonLedger.Api;
using NeonLedger.Tests.Infrastructure;

namespace NeonLedger.Tests;

/// <summary>
/// Which web pages a browser lets call the service. The service has no authentication,
/// so this list is the only thing between a page in someone's browser and their ledger.
/// </summary>
public sealed class BrowserAccessTests(PostgresServer server) : IAsyncLifetime
{
    private const string ConsoleOrigin = "http://localhost:5173";

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var connectionString = await server.CreateDatabaseAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Ledger", connectionString);
            builder.UseSetting("Cors:AllowedOrigins:0", ConsoleOrigin);
        });
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task A_configured_origin_may_send_a_write_with_an_idempotency_key()
    {
        var response = await PreflightAsync(ConsoleOrigin, "POST", "content-type,idempotency-key");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(ConsoleOrigin, Header(response, "Access-Control-Allow-Origin"));
        Assert.Contains("POST", Header(response, "Access-Control-Allow-Methods"));
        Assert.Contains("idempotency-key", Header(response, "Access-Control-Allow-Headers"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_configured_origin_may_read_the_header_that_marks_a_replay()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Origin", ConsoleOrigin);

        var response = await _client.SendAsync(request, Ct);

        Assert.Equal(ConsoleOrigin, Header(response, "Access-Control-Allow-Origin"));
        Assert.Contains(Idempotent.ReplayedHeader, Header(response, "Access-Control-Expose-Headers"));
    }

    [Fact]
    public async Task Any_other_origin_is_given_nothing()
    {
        var response = await PreflightAsync("https://elsewhere.example", "POST", "content-type,idempotency-key");

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task With_no_origins_configured_no_page_may_call_the_service()
    {
        await using var closed = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Ledger", "Host=unused");
        });
        using var client = closed.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/v1/transactions");
        request.Headers.Add("Origin", ConsoleOrigin);
        request.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await client.SendAsync(request, Ct);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private async Task<HttpResponseMessage> PreflightAsync(string origin, string method, string headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/v1/transactions");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", headers);
        return await _client.SendAsync(request, Ct);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : string.Empty;
}
