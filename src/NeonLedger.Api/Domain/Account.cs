namespace NeonLedger.Domain;

public sealed class Account
{
    public const int MaxNameLength = 120;

    private Account()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>ISO 4217 code. An account holds one currency for its whole life.</summary>
    public string Currency { get; private set; } = string.Empty;

    /// <summary>
    /// The side that increases the balance. Customer funds are a liability, so
    /// customer accounts are credit-normal; cash and settlement accounts are debit-normal.
    /// </summary>
    public Direction NormalBalance { get; private set; }

    /// <summary>When false, a posting that would take the balance below zero is rejected.</summary>
    public bool AllowNegative { get; private set; }

    /// <summary>Minor units, signed in the account's normal direction.</summary>
    public long Balance { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Account Open(string name, string currency, Direction normalBalance, bool allowNegative, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(now),
            Name = name,
            Currency = currency,
            NormalBalance = normalBalance,
            AllowNegative = allowNegative,
            CreatedAt = Timestamps.ToStoredPrecision(now),
        };

    /// <summary>The change an entry makes to this account's balance.</summary>
    public long DeltaFor(Direction direction, long amount) => direction == NormalBalance ? amount : -amount;

    /// <summary>Applies an entry and returns the balance after it.</summary>
    public long Apply(Direction direction, long amount)
    {
        Balance = checked(Balance + DeltaFor(direction, amount));
        return Balance;
    }
}
