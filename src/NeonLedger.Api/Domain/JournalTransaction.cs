namespace NeonLedger.Domain;

/// <summary>
/// One balanced set of entries. Posted once and never changed; a mistake is
/// corrected by posting a reversal.
/// </summary>
public sealed class JournalTransaction
{
    public const int MaxDescriptionLength = 255;

    private readonly List<Entry> _entries = [];

    private JournalTransaction()
    {
    }

    public Guid Id { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public string Currency { get; private set; } = string.Empty;

    public DateTimeOffset PostedAt { get; private set; }

    /// <summary>Set when this transaction is the reversal of another.</summary>
    public Guid? ReversesTransactionId { get; private set; }

    public IReadOnlyList<Entry> Entries => _entries;

    public static JournalTransaction Create(string description, string currency, Guid? reverses, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(now),
            Description = description,
            Currency = currency,
            PostedAt = Timestamps.ToStoredPrecision(now),
            ReversesTransactionId = reverses,
        };

    public void AddEntry(Guid accountId, Direction direction, long amount, long balanceAfter) =>
        _entries.Add(new Entry(Id, accountId, direction, amount, balanceAfter));
}
