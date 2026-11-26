using System.Text.Json;
using NeonLedger.Domain;
using NeonLedger.Payments;

namespace NeonLedger.Api;

public sealed record CreateAccountRequest(string? Name, string? Currency, Direction? NormalBalance, bool AllowNegative = false);

public sealed record AccountResponse(
    Guid Id,
    string Name,
    string Currency,
    Direction NormalBalance,
    bool AllowNegative,
    long Balance,
    long Held,
    long Available,
    DateTimeOffset CreatedAt)
{
    public static AccountResponse From(Account account) =>
        new(
            account.Id,
            account.Name,
            account.Currency,
            account.NormalBalance,
            account.AllowNegative,
            account.Balance,
            account.Held,
            account.Available,
            account.CreatedAt);
}

public sealed record EntryRequest(Guid AccountId, Direction? Direction, long Amount);

public sealed record PostTransactionRequest(string? Description, IReadOnlyList<EntryRequest?>? Entries);

public sealed record EntryResponse(long Id, Guid AccountId, Direction Direction, long Amount, long BalanceAfter);

public sealed record TransactionResponse(
    Guid Id,
    string Description,
    string Currency,
    DateTimeOffset PostedAt,
    Guid? ReversesTransactionId,
    IReadOnlyList<EntryResponse> Entries)
{
    public static TransactionResponse From(JournalTransaction transaction) =>
        new(
            transaction.Id,
            transaction.Description,
            transaction.Currency,
            transaction.PostedAt,
            transaction.ReversesTransactionId,
            [.. transaction.Entries.OrderBy(e => e.Id).Select(e => new EntryResponse(e.Id, e.AccountId, e.Direction, e.Amount, e.BalanceAfter))]);
}

public sealed record AccountEntryResponse(
    long Id,
    Guid TransactionId,
    string Description,
    Direction Direction,
    long Amount,
    long BalanceAfter,
    DateTimeOffset PostedAt);

/// <summary>One page of an account's entries, newest first. Pass <c>NextBefore</c> as <c>before</c> for the next page.</summary>
public sealed record EntryPage(IReadOnlyList<AccountEntryResponse> Items, long? NextBefore);

/// <summary>One event. <c>Cursor</c> is this event's position in the feed.</summary>
public sealed record EventResponse(string Cursor, string Type, DateTimeOffset OccurredAt, JsonElement Data);

/// <summary>A page of the event feed, oldest first. Pass <c>NextCursor</c> as <c>after</c> to continue.</summary>
public sealed record EventPage(IReadOnlyList<EventResponse> Items, string NextCursor);

public sealed record CreatePaymentRequest(PaymentRail? Rail, Guid SourceAccountId, Guid DestinationAccountId, long Amount, string? Description);

public sealed record ReturnPaymentRequest(string? Reason);

public sealed record PaymentResponse(
    Guid Id,
    PaymentRail Rail,
    PaymentStatus Status,
    Guid SourceAccountId,
    Guid DestinationAccountId,
    long Amount,
    string Currency,
    string Description,
    Guid? SettlementTransactionId,
    Guid? ReturnTransactionId,
    string? ReturnReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static PaymentResponse From(Payment payment) =>
        new(
            payment.Id,
            payment.Rail,
            payment.Status,
            payment.SourceAccountId,
            payment.DestinationAccountId,
            payment.Amount,
            payment.Currency,
            payment.Description,
            payment.SettlementTransactionId,
            payment.ReturnTransactionId,
            payment.ReturnReason,
            payment.CreatedAt,
            payment.UpdatedAt);
}

public sealed record AccountMismatch(Guid AccountId, long StoredBalance, long BalanceFromEntries);

public sealed record HoldMismatch(Guid AccountId, long StoredHeld, long HeldFromPayments);

public sealed record IntegrityReport(
    bool Consistent,
    long DebitsMinusCredits,
    int AccountsChecked,
    IReadOnlyList<AccountMismatch> Mismatches,
    IReadOnlyList<HoldMismatch> HoldMismatches);
