using Microsoft.EntityFrameworkCore;
using NeonLedger.Api;
using NeonLedger.Data;
using NeonLedger.Domain;
using NeonLedger.Events;
using NeonLedger.Ledger;

namespace NeonLedger.Payments;

public sealed record CreatePayment(PaymentRail Rail, Guid SourceAccountId, Guid DestinationAccountId, long Amount, string Description);

/// <summary>
/// Payments on top of the ledger. A payment never touches a balance itself: it
/// places or releases a hold, and asks the ledger to post. Like the ledger, every
/// method must run inside a database transaction that the caller commits.
///
/// Locks are always taken in the same order: the payment row first, then accounts
/// in id order.
/// </summary>
public sealed class PaymentService(LedgerDbContext db, LedgerService ledger, EventLog events, TimeProvider clock)
{
    public Task<Result<Payment>> CreateAsync(CreatePayment command, CancellationToken cancellationToken) =>
        command.Rail == PaymentRail.Instant
            ? CreateInstantAsync(command, cancellationToken)
            : CreatePendingAsync(command, cancellationToken);

    /// <summary>Moves the money for a pending payment and lifts its hold in the same step.</summary>
    public async Task<Result<Payment>> SettleAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await LockAsync(paymentId, cancellationToken);
        if (payment is null)
        {
            return LedgerError.PaymentNotFound(paymentId);
        }

        if (payment.Status != PaymentStatus.Pending)
        {
            return LedgerError.InvalidPaymentState(paymentId, "settled", $"it is {Name(payment.Status)}, not pending");
        }

        var posted = await ledger.PostAsync(
            Movement(payment.Description, payment.SourceAccountId, payment.DestinationAccountId, payment.Amount),
            new HoldRelease(payment.SourceAccountId, payment.Amount),
            cancellationToken);
        if (!posted.IsSuccess)
        {
            return posted.Error;
        }

        payment.Settle(posted.Value.Id, clock.GetUtcNow());
        return await SaveAsync(payment, EventTypes.PaymentSettled, cancellationToken);
    }

    /// <summary>Stops a pending payment and gives the held funds back.</summary>
    public async Task<Result<Payment>> CancelAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await LockAsync(paymentId, cancellationToken);
        if (payment is null)
        {
            return LedgerError.PaymentNotFound(paymentId);
        }

        if (payment.Status != PaymentStatus.Pending)
        {
            return LedgerError.InvalidPaymentState(paymentId, "canceled", $"it is {Name(payment.Status)}, not pending");
        }

        var accounts = await ledger.LockAccountsAsync([payment.SourceAccountId], cancellationToken);
        accounts[payment.SourceAccountId].ReleaseHold(payment.Amount);
        payment.Cancel(clock.GetUtcNow());
        return await SaveAsync(payment, EventTypes.PaymentCanceled, cancellationToken);
    }

    /// <summary>
    /// Sends a settled payment back by reversing its settlement. Refused if the money
    /// is no longer in the destination account; the payment then stays settled.
    /// </summary>
    public async Task<Result<Payment>> ReturnAsync(Guid paymentId, string reason, CancellationToken cancellationToken)
    {
        var payment = await LockAsync(paymentId, cancellationToken);
        if (payment is null)
        {
            return LedgerError.PaymentNotFound(paymentId);
        }

        if (payment.Rail == PaymentRail.Instant)
        {
            return LedgerError.InvalidPaymentState(paymentId, "returned", "instant payments are final");
        }

        if (payment.Status != PaymentStatus.Settled)
        {
            return LedgerError.InvalidPaymentState(paymentId, "returned", $"it is {Name(payment.Status)}, not settled");
        }

        var reversed = await ledger.ReverseAsync(payment.SettlementTransactionId!.Value, cancellationToken);
        if (!reversed.IsSuccess)
        {
            return reversed.Error;
        }

        payment.Return(reversed.Value.Id, reason, clock.GetUtcNow());
        return await SaveAsync(payment, EventTypes.PaymentReturned, cancellationToken);
    }

    private async Task<Result<Payment>> CreateInstantAsync(CreatePayment command, CancellationToken cancellationToken)
    {
        // The ledger checks the accounts, the currency and the funds, under its own locks.
        var posted = await ledger.PostAsync(
            Movement(command.Description, command.SourceAccountId, command.DestinationAccountId, command.Amount),
            cancellationToken);
        if (!posted.IsSuccess)
        {
            return posted.Error;
        }

        var now = clock.GetUtcNow();
        var payment = Payment.Create(
            command.Rail,
            command.SourceAccountId,
            command.DestinationAccountId,
            command.Amount,
            posted.Value.Currency,
            command.Description,
            now);
        payment.Settle(posted.Value.Id, now);
        db.Payments.Add(payment);
        events.Append(EventTypes.PaymentCreated, PaymentResponse.From(payment));
        return await SaveAsync(payment, EventTypes.PaymentSettled, cancellationToken);
    }

    private async Task<Result<Payment>> CreatePendingAsync(CreatePayment command, CancellationToken cancellationToken)
    {
        // Only the source is locked: it is the one whose hold changes. The destination is
        // read without a lock, because nothing about it changes until settlement and its
        // currency never does. That keeps an account many payments point at, such as a
        // settlement account, out of the way while payments are only pending.
        var accounts = await ledger.LockAccountsAsync([command.SourceAccountId], cancellationToken);
        if (!accounts.TryGetValue(command.SourceAccountId, out var source))
        {
            return LedgerError.AccountNotFound(command.SourceAccountId);
        }

        var destinationCurrency = await db.Accounts
            .Where(a => a.Id == command.DestinationAccountId)
            .Select(a => a.Currency)
            .SingleOrDefaultAsync(cancellationToken);
        if (destinationCurrency is null)
        {
            return LedgerError.AccountNotFound(command.DestinationAccountId);
        }

        if (destinationCurrency != source.Currency)
        {
            return LedgerError.CurrencyMismatch(command.DestinationAccountId, destinationCurrency, source.Currency);
        }

        if (source.Available < command.Amount && !source.AllowNegative)
        {
            return LedgerError.InsufficientFunds(source.Id);
        }

        try
        {
            source.PlaceHold(command.Amount);
        }
        catch (OverflowException)
        {
            return LedgerError.BalanceOutOfRange(source.Id);
        }

        var payment = Payment.Create(
            command.Rail,
            source.Id,
            command.DestinationAccountId,
            command.Amount,
            source.Currency,
            command.Description,
            clock.GetUtcNow());
        db.Payments.Add(payment);
        return await SaveAsync(payment, EventTypes.PaymentCreated, cancellationToken);
    }

    private async Task<Result<Payment>> SaveAsync(Payment payment, string eventType, CancellationToken cancellationToken)
    {
        events.Append(eventType, PaymentResponse.From(payment));
        await db.SaveChangesAsync(cancellationToken);
        return payment;
    }

    /// <summary>
    /// Locks the payment row, so two requests that act on one payment run one after
    /// the other and the second sees the status the first left.
    /// </summary>
    private async Task<Payment?> LockAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Payment changes must run inside a database transaction.");
        }

        var rows = await db.Payments
            .FromSql($"SELECT * FROM payments WHERE id = {paymentId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    /// <summary>
    /// Money leaving the source and arriving at the destination. For two customer
    /// accounts that is a transfer; from a customer to a settlement account it is an
    /// outgoing payment; from a settlement account to a customer it is a deposit.
    /// </summary>
    private static PostTransaction Movement(string description, Guid source, Guid destination, long amount) =>
        new(description, [new EntryLine(source, Direction.Debit, amount), new EntryLine(destination, Direction.Credit, amount)]);

    private static string Name(PaymentStatus status) => status.ToString().ToLowerInvariant();
}
