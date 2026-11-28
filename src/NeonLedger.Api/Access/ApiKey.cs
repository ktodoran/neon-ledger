using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NeonLedger.Access;

public enum AccessLevel
{
    /// <summary>May read accounts, entries, payments, events and the integrity report.</summary>
    Read,

    /// <summary>May also open accounts, post transactions and move payments.</summary>
    Write,
}

/// <summary>
/// A credential for a caller. The ledger keeps the key's id and a hash of its secret,
/// never the secret: the full key exists only in the output of the command that made it.
/// </summary>
public sealed partial class ApiKey
{
    public const int IdLength = 12;

    /// <summary>The public half of the key. Safe to log, list and use to revoke.</summary>
    public string Id { get; private set; } = string.Empty;

    public Guid CallerId { get; private set; }

    public Caller Caller { get; private set; } = null!;

    /// <summary>SHA-256 of the secret, in hex.</summary>
    public string SecretHash { get; private set; } = string.Empty;

    public AccessLevel Access { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Makes a key and returns it with the one copy of its full text.</summary>
    public static (ApiKey Key, string Token) Issue(Caller caller, AccessLevel access, DateTimeOffset now)
    {
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(IdLength / 2));
        var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var key = new ApiKey
        {
            Id = id,
            CallerId = caller.Id,
            Caller = caller,
            SecretHash = Hash(secret),
            Access = access,
            CreatedAt = Domain.Timestamps.ToStoredPrecision(now),
        };
        return (key, $"nl_{id}_{secret}");
    }

    /// <summary>Splits a presented key into its id and secret. False for anything that is not shaped like one.</summary>
    public static bool TryParse(string token, out string id, out string secret)
    {
        var match = TokenShape().Match(token);
        id = match.Groups["id"].Value;
        secret = match.Groups["secret"].Value;
        return match.Success;
    }

    /// <summary>
    /// Whether <paramref name="secret"/> is this key's secret. The secret is 256 random bits,
    /// so a plain fast hash is enough: there is nothing to guess that a slow hash would protect.
    /// </summary>
    public static bool Matches(string storedHash, string secret) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(storedHash), Encoding.ASCII.GetBytes(Hash(secret)));

    public void Revoke(DateTimeOffset now) => RevokedAt ??= Domain.Timestamps.ToStoredPrecision(now);

    private static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(secret)));

    [GeneratedRegex("^nl_(?<id>[0-9a-f]{12})_(?<secret>[0-9a-f]{64})$")]
    private static partial Regex TokenShape();
}
