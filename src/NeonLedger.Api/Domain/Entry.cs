namespace NeonLedger.Domain;

public sealed class Entry
{
    private Entry()
    {
    }

    internal Entry(Guid transactionId, Guid accountId, Direction direction, long amount, long balanceAfter)
    {
        TransactionId = transactionId;
        AccountId = accountId;
        Direction = direction;
        Amount = amount;
        BalanceAfter = balanceAfter;
    }

    /// <summary>Assigned by the database. Increases in posting order within an account.</summary>
    public long Id { get; private set; }

    public Guid TransactionId { get; private set; }

    public Guid AccountId { get; private set; }

    public Direction Direction { get; private set; }

    /// <summary>Minor units, always positive. The direction carries the sign.</summary>
    public long Amount { get; private set; }

    /// <summary>The account's balance immediately after this entry.</summary>
    public long BalanceAfter { get; private set; }
}
