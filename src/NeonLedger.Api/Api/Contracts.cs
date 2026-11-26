using NeonLedger.Domain;

namespace NeonLedger.Api;

public sealed record CreateAccountRequest(string? Name, string? Currency, Direction? NormalBalance, bool AllowNegative = false);

public sealed record AccountResponse(
    Guid Id,
    string Name,
    string Currency,
    Direction NormalBalance,
    bool AllowNegative,
    long Balance,
    DateTimeOffset CreatedAt)
{
    public static AccountResponse From(Account account) =>
        new(account.Id, account.Name, account.Currency, account.NormalBalance, account.AllowNegative, account.Balance, account.CreatedAt);
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

public sealed record AccountMismatch(Guid AccountId, long StoredBalance, long BalanceFromEntries);

public sealed record IntegrityReport(bool Consistent, long DebitsMinusCredits, int AccountsChecked, IReadOnlyList<AccountMismatch> Mismatches);
