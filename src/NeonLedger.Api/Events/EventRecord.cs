namespace NeonLedger.Events;

/// <summary>
/// Something that happened, written in the same database transaction as the change
/// it describes. If the change commits, the event exists; if it rolls back, it does not.
/// </summary>
public sealed class EventRecord
{
    public const int MaxTypeLength = 64;

    private EventRecord()
    {
    }

    /// <summary>Assigned by the database.</summary>
    public long Id { get; private set; }

    /// <summary>
    /// The id of the database transaction that wrote this event, assigned by the
    /// database. The feed orders by it; see <see cref="EventLog"/> for why.
    /// </summary>
    public long Txid { get; private set; }

    public string Type { get; private set; } = string.Empty;

    /// <summary>JSON, in the same shape the API returns for the thing that changed.</summary>
    public string Payload { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }

    public static EventRecord Create(string type, string payload, DateTimeOffset now) =>
        new() { Type = type, Payload = payload, OccurredAt = now };
}
