using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NeonLedger.Data;
using NeonLedger.Domain;

namespace NeonLedger.Events;

public sealed record StoredEvent(long Id, long Txid, string Type, string Payload, DateTimeOffset OccurredAt)
{
    public EventCursor Cursor => new(Txid, Id);
}

/// <summary>
/// Writes events alongside the changes they describe, and reads them back as a feed
/// a consumer can follow with a cursor.
///
/// The obvious feed, "events with an id greater than the last one I saw", loses
/// events. Ids are handed out when a row is inserted, not when it commits, so a
/// slow transaction can commit an event with a lower id after a consumer has
/// already read past it. The feed here avoids that in two steps:
///
/// 1. It only returns events from database transactions older than every
///    transaction still in progress. That set can no longer grow: anything that
///    commits later has a higher transaction id.
/// 2. It orders by (transaction id, event id), so within that settled set the
///    order never changes and a cursor never skips anything.
///
/// The cost is that one long-running write transaction holds the feed back until
/// it finishes. Events are delayed, never lost or reordered.
/// </summary>
public sealed class EventLog(LedgerDbContext db, TimeProvider clock, IOptions<JsonOptions> json)
{
    private readonly JsonSerializerOptions _json = json.Value.SerializerOptions;

    /// <summary>Queues an event to be saved with the caller's next <c>SaveChanges</c>.</summary>
    public void Append<T>(string type, T data) =>
        db.Events.Add(EventRecord.Create(
            type,
            JsonSerializer.Serialize(data, _json),
            Timestamps.ToStoredPrecision(clock.GetUtcNow())));

    public Task<List<StoredEvent>> ReadAsync(EventCursor after, int limit, CancellationToken cancellationToken) =>
        db.Database
            .SqlQuery<StoredEvent>(
                $"""
                SELECT id, txid, type, payload::text AS payload, occurred_at
                FROM events
                WHERE (txid, id) > ({after.Txid}, {after.Id})
                  AND txid < pg_snapshot_xmin(pg_current_snapshot())::text::bigint
                ORDER BY txid, id
                LIMIT {limit}
                """)
            .ToListAsync(cancellationToken);
}
