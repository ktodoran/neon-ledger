using Microsoft.EntityFrameworkCore;
using NeonLedger.Data;

namespace NeonLedger.Access;

public sealed record IssuedKey(string KeyId, Guid CallerId, string CallerName, AccessLevel Access, string Token);

public sealed record KeyListing(string KeyId, string CallerName, AccessLevel Access, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);

/// <summary>Creates, lists and revokes API keys. Used by the <c>keys</c> command; there is no HTTP surface for it.</summary>
public sealed class KeyIssuer(LedgerDbContext db, TimeProvider clock)
{
    /// <summary>Issues a key to the caller with this name, creating the caller if it is new.</summary>
    public async Task<IssuedKey> CreateAsync(string callerName, AccessLevel access, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var caller = await db.Callers.SingleOrDefaultAsync(c => c.Name == callerName, cancellationToken);
        if (caller is null)
        {
            caller = Caller.Create(callerName, now);
            db.Callers.Add(caller);
        }

        var (key, token) = ApiKey.Issue(caller, access, now);
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(cancellationToken);
        return new IssuedKey(key.Id, caller.Id, caller.Name, key.Access, token);
    }

    public Task<List<KeyListing>> ListAsync(CancellationToken cancellationToken) =>
        db.ApiKeys.AsNoTracking()
            .OrderBy(k => k.Caller.Name)
            .ThenBy(k => k.CreatedAt)
            .Select(k => new KeyListing(k.Id, k.Caller.Name, k.Access, k.CreatedAt, k.RevokedAt))
            .ToListAsync(cancellationToken);

    /// <summary>Revokes a key. It stops working with the next request. False if there is no such key.</summary>
    public async Task<bool> RevokeAsync(string keyId, CancellationToken cancellationToken)
    {
        var key = await db.ApiKeys.SingleOrDefaultAsync(k => k.Id == keyId, cancellationToken);
        if (key is null)
        {
            return false;
        }

        key.Revoke(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
