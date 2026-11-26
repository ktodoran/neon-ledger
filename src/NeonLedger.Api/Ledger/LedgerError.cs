namespace NeonLedger.Ledger;

/// <summary>A request the ledger refused, with the HTTP status that describes it.</summary>
public sealed record LedgerError(string Code, string Message, int Status)
{
    public static LedgerError AccountNotFound(Guid id) =>
        new("account_not_found", $"Account {id} does not exist.", StatusCodes.Status422UnprocessableEntity);

    public static LedgerError CurrencyMismatch(Guid id, string accountCurrency, string transactionCurrency) =>
        new(
            "currency_mismatch",
            $"Account {id} holds {accountCurrency}; every entry in a transaction must be in one currency ({transactionCurrency}).",
            StatusCodes.Status422UnprocessableEntity);

    public static LedgerError InsufficientFunds(Guid id) =>
        new("insufficient_funds", $"Account {id} does not have enough funds for this transaction.", StatusCodes.Status422UnprocessableEntity);

    public static LedgerError BalanceOutOfRange(Guid id) =>
        new("balance_out_of_range", $"Account {id} cannot hold a balance this large.", StatusCodes.Status422UnprocessableEntity);

    public static LedgerError TransactionNotFound(Guid id) =>
        new("transaction_not_found", $"Transaction {id} does not exist.", StatusCodes.Status404NotFound);

    public static LedgerError AlreadyReversed(Guid id, Guid reversalId) =>
        new("already_reversed", $"Transaction {id} was already reversed by {reversalId}.", StatusCodes.Status409Conflict);

    public static LedgerError ReversalNotReversible(Guid id) =>
        new("reversal_not_reversible", $"Transaction {id} is itself a reversal and cannot be reversed.", StatusCodes.Status409Conflict);
}
