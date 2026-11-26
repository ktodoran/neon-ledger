using Microsoft.EntityFrameworkCore;
using NeonLedger.Api;
using NeonLedger.Data;
using NeonLedger.Domain;
using NeonLedger.Events;

namespace NeonLedger.Ledger;

public sealed record EntryLine(Guid AccountId, Direction Direction, long Amount);

public sealed record PostTransaction(string Description, IReadOnlyList<EntryLine> Entries);

/// <summary>A hold to lift in the same step as a posting, so the held funds can pay for it.</summary>
public readonly record struct HoldRelease(Guid AccountId, long Amount);

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
        PostAsync(command, reverses: null, release: null, cancellationToken);

    /// <summary>Posts a transaction and releases a hold on one of its accounts at the same time.</summary>
    public Task<Result<JournalTransaction>> PostAsync(PostTransaction command, HoldRelease release, CancellationToken cancellationToken) =>
        PostAsync(command, reverses: null, release, cancellationToken);

    /// <summary>
    /// Locks accounts for the rest of the database transaction, always in id order.
    /// Everything that changes a balance or a hold goes through here, so two writers
    /// that share accounts queue instead of deadlocking, and each sees what the other left.
    ///
    /// The lock is FOR NO KEY UPDATE, the one an ordinary UPDATE takes. It excludes every
    /// other writer of the row, but not a foreign-key check, so a transaction that only
    /// refers to an account (a new pending payment naming its destination) is not held up.
    /// </summary>
    public async Task<Dictionary<Guid, Account>> LockAccountsAsync(IEnumerable<Guid> accountIds, CancellationToken cancellationToken)
    {
        EnsureInTransaction();
        var ids = accountIds.Distinct().ToArray();
        return await db.Accounts
            .FromSql($"SELECT * FROM accounts WHERE id = ANY({ids}) ORDER BY id FOR NO KEY UPDATE")
            .ToDictionaryAsync(a => a.Id, cancellationToken);
    }

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
        return await PostAsync(new PostTransaction($"Reversal of {transactionId}", mirrored), transactionId, release: null, cancellationToken);
    }

    private async Task<Result<JournalTransaction>> PostAsync(
        PostTransaction command,
        Guid? reverses,
        HoldRelease? release,
        CancellationToken cancellationToken)
    {
        EnsureInTransaction();

        var ids = command.Entries.Select(e => e.AccountId).Distinct().ToArray();
        var accounts = await LockAccountsAsync(ids, cancellationToken);

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
        // same account as long as it ends with nothing overdrawn. Held funds do not
        // count as spendable, except by the payment whose hold is being released here.
        foreach (var account in accounts.Values)
        {
            var held = account.Held - (release?.AccountId == account.Id ? release.Value.Amount : 0);
            if (balances[account.Id] - held < 0 && !account.AllowNegative)
            {
                return LedgerError.InsufficientFunds(account.Id);
            }
        }

        if (release is { } lifted)
        {
            accounts[lifted.AccountId].ReleaseHold(lifted.Amount);
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
