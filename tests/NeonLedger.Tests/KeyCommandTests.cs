using System.Net;
using NeonLedger.Access;
using NeonLedger.Tests.Infrastructure;

namespace NeonLedger.Tests;

/// <summary>The <c>keys</c> command, run the way the program runs it, with its output captured.</summary>
public sealed class KeyCommandTests(LedgerApp app) : IClassFixture<LedgerApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Creating_a_key_prints_it_once_and_it_works()
    {
        var (exitCode, output) = await RunAsync("keys", "create", "billing-service");

        var token = Assert.Single(output.Split('\n', StringSplitOptions.TrimEntries), line => line.StartsWith("nl_", StringComparison.Ordinal));
        Assert.Equal(0, exitCode);
        Assert.Contains("\"billing-service\" (read and write)", output);
        Assert.Contains("only time it is shown", output);
        using var client = app.CreateClient(token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/accounts", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_quiet_key_is_the_only_thing_printed_so_a_script_can_capture_it()
    {
        var (exitCode, output) = await RunAsync("keys", "create", "ci", "--quiet");

        Assert.Equal(0, exitCode);
        Assert.True(ApiKey.TryParse(output.Trim(), out _, out _));
    }

    [Fact]
    public async Task A_read_only_key_is_created_and_listed_as_one()
    {
        var (_, created) = await RunAsync("keys", "create", "auditor", "--read-only", "--quiet");
        Assert.True(ApiKey.TryParse(created.Trim(), out var id, out var secret));

        var (exitCode, listed) = await RunAsync("keys", "list");

        var line = Assert.Single(listed.Split('\n'), line => line.StartsWith(id, StringComparison.Ordinal));
        Assert.Equal(0, exitCode);
        Assert.Contains("auditor", line);
        Assert.Contains("read only", line);
        Assert.Contains("active", line);
        Assert.DoesNotContain(secret, listed);
    }

    [Fact]
    public async Task Revoking_a_key_stops_it_and_the_list_says_so()
    {
        var (_, created) = await RunAsync("keys", "create", "contractor", "--quiet");
        var token = created.Trim();
        Assert.True(ApiKey.TryParse(token, out var id, out _));

        var (exitCode, output) = await RunAsync("keys", "revoke", id);

        Assert.Equal(0, exitCode);
        Assert.Contains($"Key {id} is revoked.", output);
        using var client = app.CreateClient(token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/accounts", Ct)).StatusCode);
        var (_, listed) = await RunAsync("keys", "list");
        Assert.Contains("revoked", Assert.Single(listed.Split('\n'), line => line.StartsWith(id, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("keys")]
    [InlineData("keys", "create")]
    [InlineData("keys", "create", "--quiet")]
    [InlineData("keys", "create", "billing", "--admin")]
    [InlineData("keys", "revoke")]
    [InlineData("keys", "revoke", "000000000000")]
    [InlineData("keys", "delete", "everything")]
    public async Task A_command_that_makes_no_sense_changes_nothing_and_fails(params string[] args)
    {
        var (_, before) = await RunAsync("keys", "list");

        var (exitCode, _) = await RunAsync(args);

        var (_, after) = await RunAsync("keys", "list");
        Assert.Equal(2, exitCode);
        Assert.Equal(before, after);
    }

    private async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        await using var scope = app.CreateScope();
        using var output = new StringWriter();
        var exitCode = await AdminCommands.RunAsync(args, scope.ServiceProvider, output, Ct);
        return (exitCode, output.ToString());
    }
}
