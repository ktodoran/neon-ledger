namespace NeonLedger.Access;

/// <summary>
/// Something that calls the ledger: a service, a job, the console. It is what an API key
/// belongs to and what an idempotency key is scoped to, so a caller can replace its key
/// without losing the protection its earlier requests had.
/// </summary>
public sealed class Caller
{
    public const int MaxNameLength = 80;

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public static Caller Create(string name, DateTimeOffset now) =>
        new() { Id = Guid.CreateVersion7(now), Name = name, CreatedAt = Domain.Timestamps.ToStoredPrecision(now) };
}
