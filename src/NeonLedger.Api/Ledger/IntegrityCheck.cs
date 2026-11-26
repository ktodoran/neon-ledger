using System.Data;
using Microsoft.EntityFrameworkCore;
using NeonLedger.Api;
using NeonLedger.Data;

namespace NeonLedger.Ledger;

/// <summary>
/// Recomputes every balance from the entries, and every hold from the pending
/// payments, and compares them with the stored values.
/// It reads the whole entries table, so it is an operational check, not something
/// to call on a request path.
/// </summary>
public sealed class IntegrityCheck(LedgerDbContext db)
{
    public async Task<IntegrityReport> RunAsync(CancellationToken cancellationToken)
    {
        // One snapshot for every query here, so postings that commit while the check
        // runs cannot make it report a mismatch that never existed.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        var mismatches = await db.Database
            .SqlQuery<AccountMismatch>(
                $"""
                SELECT a.id AS account_id,
                       a.balance AS stored_balance,
                       COALESCE(SUM(CASE WHEN e.direction = a.normal_balance THEN e.amount ELSE -e.amount END), 0)::bigint AS balance_from_entries
                FROM accounts a
                LEFT JOIN entries e ON e.account_id = a.id
                GROUP BY a.id
                HAVING a.balance <> COALESCE(SUM(CASE WHEN e.direction = a.normal_balance THEN e.amount ELSE -e.amount END), 0)
                ORDER BY a.id
                """)
            .ToListAsync(cancellationToken);

        var holdMismatches = await db.Database
            .SqlQuery<HoldMismatch>(
                $"""
                SELECT a.id AS account_id,
                       a.held AS stored_held,
                       COALESCE(SUM(p.amount), 0)::bigint AS held_from_payments
                FROM accounts a
                LEFT JOIN payments p ON p.source_account_id = a.id AND p.status = 'pending'
                GROUP BY a.id
                HAVING a.held <> COALESCE(SUM(p.amount), 0)
                ORDER BY a.id
                """)
            .ToListAsync(cancellationToken);

        var debitsMinusCredits = await db.Database
            .SqlQuery<long>(
                $"""
                SELECT COALESCE(SUM(CASE direction WHEN 'debit' THEN amount ELSE -amount END), 0)::bigint AS "Value"
                FROM entries
                """)
            .SingleAsync(cancellationToken);

        var accounts = await db.Accounts.CountAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new IntegrityReport(
            mismatches.Count == 0 && holdMismatches.Count == 0 && debitsMinusCredits == 0,
            debitsMinusCredits,
            accounts,
            mismatches,
            holdMismatches);
    }
}
