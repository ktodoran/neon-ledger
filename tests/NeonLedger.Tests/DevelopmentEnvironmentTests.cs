using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NeonLedger.Tests;

/// <summary>
/// Every other test runs the application as it runs in production. These cover the
/// places where <c>dotnet run</c> on a developer's machine could behave differently.
/// </summary>
public sealed class DevelopmentEnvironmentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_request_that_cannot_be_read_is_the_callers_mistake_not_a_server_error()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Ledger", "Host=unused");
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/events?limit=many", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_console_may_call_a_ledger_started_for_development()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Ledger", "Host=unused");
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/v1/transactions");
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        var response = await client.SendAsync(request, Ct);

        Assert.Equal("http://localhost:5173", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }
}
