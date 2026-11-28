using Microsoft.EntityFrameworkCore;
using NeonLedger.Access;
using NeonLedger.Data;

namespace NeonLedger;

/// <summary>
/// What the program does when it is run with a command in place of serving requests:
/// <c>migrate</c>, and <c>keys create | list | revoke</c>. Each runs once and exits.
///
/// Keys are managed here and not over HTTP. Whoever can run the program against the
/// database already has more access than any key could grant, so there is no question
/// of who may issue the first one.
/// </summary>
public static class AdminCommands
{
    private const string Usage = """
        Commands:
          migrate                                        Apply pending database migrations.
          keys create <caller> [--read-only] [--quiet]   Issue an API key. The key is printed once.
          keys list                                      List keys by caller. Secrets are not stored.
          keys revoke <key-id>                           Stop a key working, from the next request on.
        """;

    public static bool Handles(string[] args) => args is ["migrate" or "keys", ..];

    /// <summary>Runs the command and returns the process exit code: 0, or 2 for a command that made no sense.</summary>
    public static async Task<int> RunAsync(string[] args, IServiceProvider services, TextWriter output, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var keys = scope.ServiceProvider.GetRequiredService<KeyIssuer>();

        switch (args)
        {
            case ["migrate"]:
                var database = scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database;
                var before = (await database.GetAppliedMigrationsAsync(cancellationToken)).Count();
                await database.MigrateAsync(cancellationToken);
                var applied = (await database.GetAppliedMigrationsAsync(cancellationToken)).Count() - before;
                await output.WriteLineAsync(applied == 0 ? "Database is up to date." : $"Applied {applied} migration(s).");
                return 0;

            case ["keys", "create", var name, .. var options] when options.All(o => o is "--read-only" or "--quiet"):
                var callerName = name.Trim();
                if (callerName.Length is 0 or > Caller.MaxNameLength || callerName.StartsWith('-'))
                {
                    await output.WriteLineAsync($"A caller name is 1 to {Caller.MaxNameLength} characters, such as billing-service.");
                    return 2;
                }

                var access = options.Contains("--read-only") ? AccessLevel.Read : AccessLevel.Write;
                var issued = await keys.CreateAsync(callerName, access, cancellationToken);
                if (options.Contains("--quiet"))
                {
                    await output.WriteLineAsync(issued.Token);
                    return 0;
                }

                await output.WriteLineAsync($"Key {issued.KeyId} for caller \"{issued.CallerName}\" ({Describe(issued.Access)}):");
                await output.WriteLineAsync();
                await output.WriteLineAsync($"  {issued.Token}");
                await output.WriteLineAsync();
                await output.WriteLineAsync("This is the only time it is shown. The ledger keeps a hash of it, not the key.");
                return 0;

            case ["keys", "list"]:
                var listed = await keys.ListAsync(cancellationToken);
                if (listed.Count == 0)
                {
                    await output.WriteLineAsync("No keys. Create one with: keys create <caller>");
                    return 0;
                }

                foreach (var key in listed)
                {
                    var state = key.RevokedAt is { } revoked ? $"revoked {revoked:u}" : "active";
                    await output.WriteLineAsync($"{key.KeyId}  {key.CallerName,-24}  {Describe(key.Access),-14}  created {key.CreatedAt:u}  {state}");
                }

                return 0;

            case ["keys", "revoke", var keyId]:
                if (await keys.RevokeAsync(keyId, cancellationToken))
                {
                    await output.WriteLineAsync($"Key {keyId} is revoked.");
                    return 0;
                }

                await output.WriteLineAsync($"There is no key {keyId}. `keys list` shows the ids.");
                return 2;

            default:
                await output.WriteLineAsync(Usage);
                return 2;
        }
    }

    private static string Describe(AccessLevel access) => access == AccessLevel.Write ? "read and write" : "read only";
}
