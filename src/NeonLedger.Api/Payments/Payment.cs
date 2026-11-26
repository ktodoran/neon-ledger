using NeonLedger.Domain;

namespace NeonLedger.Payments;

/// <summary>How a payment travels, which decides when it is final.</summary>
public enum PaymentRail
{
    /// <summary>Final the moment it is accepted. It cannot be canceled or returned.</summary>
    Instant,

    /// <summary>
    /// Accepted now, settled later, and able to come back after that: the shape of an
    /// ACH payment. Funds are held from acceptance until it settles or is canceled.
    /// </summary>
    Ach,
}

public enum PaymentStatus
{
    /// <summary>Accepted and holding funds, but no money has moved.</summary>
    Pending,

    /// <summary>The money has moved.</summary>
    Settled,

    /// <summary>Stopped before settlement. The hold was released and no money moved.</summary>
    Canceled,

    /// <summary>Settled and then sent back. The settlement was reversed.</summary>
    Returned,
}

public sealed class Payment
{
    public const int MaxDescriptionLength = JournalTransaction.MaxDescriptionLength;
    public const int MaxReturnReasonLength = 64;

    private Payment()
    {
    }

    public Guid Id { get; private set; }

    public PaymentRail Rail { get; private set; }

    public PaymentStatus Status { get; private set; }

    public Guid SourceAccountId { get; private set; }

    public Guid DestinationAccountId { get; private set; }

    public long Amount { get; private set; }

    public string Currency { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    /// <summary>The ledger transaction that moved the money. Set once the payment settles.</summary>
    public Guid? SettlementTransactionId { get; private set; }

    /// <summary>The ledger transaction that reversed the settlement. Set if the payment is returned.</summary>
    public Guid? ReturnTransactionId { get; private set; }

    /// <summary>Why it came back, for example an ACH return code such as R01.</summary>
    public string? ReturnReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Payment Create(
        PaymentRail rail,
        Guid sourceAccountId,
        Guid destinationAccountId,
        long amount,
        string currency,
        string description,
        DateTimeOffset now)
    {
        var at = Timestamps.ToStoredPrecision(now);
        return new Payment
        {
            Id = Guid.CreateVersion7(now),
            Rail = rail,
            Status = PaymentStatus.Pending,
            SourceAccountId = sourceAccountId,
            DestinationAccountId = destinationAccountId,
            Amount = amount,
            Currency = currency,
            Description = description,
            CreatedAt = at,
            UpdatedAt = at,
        };
    }

    public void Settle(Guid transactionId, DateTimeOffset now)
    {
        Require(PaymentStatus.Pending);
        Status = PaymentStatus.Settled;
        SettlementTransactionId = transactionId;
        UpdatedAt = Timestamps.ToStoredPrecision(now);
    }

    public void Cancel(DateTimeOffset now)
    {
        Require(PaymentStatus.Pending);
        Status = PaymentStatus.Canceled;
        UpdatedAt = Timestamps.ToStoredPrecision(now);
    }

    public void Return(Guid transactionId, string reason, DateTimeOffset now)
    {
        Require(PaymentStatus.Settled);
        Status = PaymentStatus.Returned;
        ReturnTransactionId = transactionId;
        ReturnReason = reason;
        UpdatedAt = Timestamps.ToStoredPrecision(now);
    }

    private void Require(PaymentStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"Payment {Id} is {Status}; expected {expected}.");
        }
    }
}
