using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NeonLedger.Data;

namespace NeonLedger.Idempotency;

/// <summary>The response to send, and whether it came from an earlier request with the same key.</summary>
public sealed record IdempotentResponse(int StatusCode, string Body, bool Replayed);

public enum IdempotencyOutcome
{
    Completed,

    /// <summary>The key was first used with a different request.</summary>
    KeyReused,
}

public sealed record IdempotencyResult(IdempotencyOutcome Outcome, IdempotentResponse? Response);

/// <summary>
/// Runs a write at most once per caller and idempotency key.
///
/// The key row, the write and the stored response are committed in one database
/// transaction. A second request with the same key blocks on the key's unique index
/// until the first one finishes, then receives the first one's response. If the first
/// one fails unexpectedly the whole transaction rolls back, the key is free again, and
/// a retry runs the write for real.
/// </summary>
public sealed class IdempotentExecutor(LedgerDbContext db, TimeProvider clock, IOptions<JsonOptions> json)
{
    private readonly JsonSerializerOptions _json = json.Value.SerializerOptions;

    public async Task<IdempotencyResult> RunAsync<TRequest>(
        Guid callerId,
        string key,
        string operation,
        TRequest request,
        Func<CancellationToken, Task<(int StatusCode, object Body)>> action,
        CancellationToken cancellationToken)
    {
        var hash = Hash(operation, request);
        var now = clock.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var claimed = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO idempotency_keys (caller_id, key, operation, request_hash, created_at)
            VALUES ({callerId}, {key}, {operation}, {hash}, {now})
            ON CONFLICT (caller_id, key) DO NOTHING
            """,
            cancellationToken);

        if (claimed == 0)
        {
            var stored = await db.IdempotencyKeys.AsNoTracking().SingleAsync(r => r.CallerId == callerId && r.Key == key, cancellationToken);
            await transaction.RollbackAsync(cancellationToken);

            if (stored.RequestHash != hash || stored.Operation != operation)
            {
                return new IdempotencyResult(IdempotencyOutcome.KeyReused, null);
            }

            return new IdempotencyResult(
                IdempotencyOutcome.Completed,
                new IdempotentResponse(stored.StatusCode!.Value, stored.ResponseBody!, Replayed: true));
        }

        var (statusCode, body) = await action(cancellationToken);
        var serialized = JsonSerializer.Serialize(body, body.GetType(), _json);

        await db.Database.ExecuteSqlAsync(
            $"UPDATE idempotency_keys SET status_code = {statusCode}, response_body = {serialized} WHERE caller_id = {callerId} AND key = {key}",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new IdempotencyResult(IdempotencyOutcome.Completed, new IdempotentResponse(statusCode, serialized, Replayed: false));
    }

    private string Hash<TRequest>(string operation, TRequest request)
    {
        var payload = operation + "\n" + JsonSerializer.Serialize(request, _json);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
