using Microsoft.EntityFrameworkCore;
using NeonLedger.Api;
using NeonLedger.Data;
using NeonLedger.Domain;
using NeonLedger.Events;

namespace NeonLedger.Ledger;

public sealed record EntryLine(Guid AccountId, Direction Direction, long Amount);

public sealed record PostTransaction(string Description, IReadOnlyList<EntryLine> Entries);

/// <summary>
/// Posts transactions. Every method here must run inside a database transaction
/// that the caller commits: the account rows are locked until then.
/// </summary>
public sealed class LedgerService(LedgerDbContext db, TimeProvider clock, EventLog events)
{
    /// <summary>The largest single entry, in minor units. Keeps balances far from overflow.</summary>
    public const long MaxAmount = 100_000_000_000_000;

    public const int MaxEntries = 100;

    /// <summary>Posts a balanced transaction. The command is assumed to have passed <see cref="PostTransactionValidator"/>.</summary>
    public Task<Result<JournalTransaction>> PostAsync(PostTransaction command, CancellationToken cancellationToken) =>
        PostAsync(command, reverses: null, cancellationToken);

    /// <summary>Posts the mirror image of a transaction. A transaction can be reversed once.</summary>
    public async Task<Result<JournalTransaction>> ReverseAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        EnsureInTransaction();

        // Lock the original so two reversals of it cannot both pass the check below.
        var locked = await db.Transactions
            .FromSql($"SELECT * FROM transactions WHERE id = {transactionId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        var original = locked.SingleOrDefault();
        if (original is null)
        {
            return LedgerError.TransactionNotFound(transactionId);
        }

        if (original.ReversesTransactionId is not null)
        {
            return LedgerError.ReversalNotReversible(transactionId);
        }

        var existing = await db.Transactions
            .Where(t => t.ReversesTransactionId == transactionId)
            .Select(t => (Guid?)t.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is { } reversalId)
        {
            return LedgerError.AlreadyReversed(transactionId, reversalId);
        }

        await db.Entry(original).Collection(t => t.Entries).LoadAsync(cancellationToken);
        var mirrored = original.Entries
            .OrderBy(e => e.Id)
            .Select(e => new EntryLine(e.AccountId, e.Direction == Direction.Debit ? Direction.Credit : Direction.Debit, e.Amount))
            .ToList();
        return await PostAsync(new PostTransaction($"Reversal of {transactionId}", mirrored), transactionId, cancellationToken);
    }

    private async Task<Result<JournalTransaction>> PostAsync(PostTransaction command, Guid? reverses, CancellationToken cancellationToken)
    {
        EnsureInTransaction();

        // Lock every account this transaction touches, always in id order. Two transactions
        // that share accounts then queue instead of deadlocking, and each one sees the
        // balance the other left behind.
        var ids = command.Entries.Select(e => e.AccountId).Distinct().ToArray();
        var accounts = await db.Accounts
            .FromSql($"SELECT * FROM accounts WHERE id = ANY({ids}) ORDER BY id FOR UPDATE")
            .ToDictionaryAsync(a => a.Id, cancellationToken);

        foreach (var id in ids)
        {
            if (!accounts.ContainsKey(id))
            {
                return LedgerError.AccountNotFound(id);
            }
        }

        var currency = accounts[command.Entries[0].AccountId].Currency;
        foreach (var account in accounts.Values)
        {
            if (account.Currency != currency)
            {
                return LedgerError.CurrencyMismatch(account.Id, account.Currency, currency);
            }
        }

        // Work out every balance before touching an entity, so a refused transaction
        // leaves nothing half-applied in the change tracker.
        var balances = accounts.ToDictionary(pair => pair.Key, pair => pair.Value.Balance);
        foreach (var line in command.Entries)
        {
            var account = accounts[line.AccountId];
            try
            {
                balances[account.Id] = checked(balances[account.Id] + account.DeltaFor(line.Direction, line.Amount));
            }
            catch (OverflowException)
            {
                return LedgerError.BalanceOutOfRange(account.Id);
            }
        }

        // Checked on the final balances, so one transaction may debit and credit the
        // same account as long as it ends at or above zero.
        foreach (var account in accounts.Values)
        {
            if (balances[account.Id] < 0 && !account.AllowNegative)
            {
                return LedgerError.InsufficientFunds(account.Id);
            }
        }

        var transaction = JournalTransaction.Create(command.Description, currency, reverses, clock.GetUtcNow());
        foreach (var line in command.Entries)
        {
            var balanceAfter = accounts[line.AccountId].Apply(line.Direction, line.Amount);
            transaction.AddEntry(line.AccountId, line.Direction, line.Amount, balanceAfter);
        }

        db.Transactions.Add(transaction);
        await db.SaveChangesAsync(cancellationToken);

        // Saved second because the event carries the entry ids the first save assigned.
        // Both saves are inside the caller's database transaction.
        events.Append(EventTypes.TransactionPosted, TransactionResponse.From(transaction));
        await db.SaveChangesAsync(cancellationToken);
        return transaction;
    }

    private void EnsureInTransaction()
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Ledger postings must run inside a database transaction.");
        }
    }
}
