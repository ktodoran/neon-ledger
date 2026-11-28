using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NeonLedger.Access;
using NeonLedger.Data;
using NeonLedger.Tests.Infrastructure;

namespace NeonLedger.Tests;

/// <summary>
/// Every other test runs the application as it runs in production. These cover the
/// places where <c>dotnet run</c> on a developer's machine could behave differently.
/// </summary>
public sealed class DevelopmentEnvironmentTests(PostgresServer server) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var connectionString = await server.CreateDatabaseAsync();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Ledger", connectionString);
        });

        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.MigrateAsync(Ct);
        var key = await scope.ServiceProvider.GetRequiredService<KeyIssuer>().CreateAsync("developer", AccessLevel.Write, Ct);
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new("Bearer", key.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task A_request_that_cannot_be_read_is_the_callers_mistake_not_a_server_error()
    {
        var response = await _client.GetAsync("/v1/events?limit=many", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_console_may_call_a_ledger_started_for_development_and_send_it_a_key()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/v1/transactions");
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,idempotency-key");

        var response = await _client.SendAsync(request, Ct);

        Assert.Equal("http://localhost:5173", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("authorization", string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task There_is_no_key_that_works_without_being_issued()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync("/v1/accounts", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
