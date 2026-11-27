import { titleOf } from '../ledger/view.js';
import { formatMoney } from '../lib/money.js';
import { nameOf } from '../outcome.js';
import { Panel } from './Panel.jsx';

const SHOWN = 40;

export const timeOf = (iso) => new Date(iso).toLocaleTimeString('en-US', { hour12: false });

export function Journal({ accounts, transactions }) {
  const shown = transactions.slice(0, SHOWN);
  return (
    <Panel title="Journal" note="Newest first · never edited, only added to" className="journal">
      {/* A scrolling region has to be reachable by keyboard to be read. */}
      <div className="scroll" tabIndex={0} role="group" aria-label="Journal entries">
        {shown.length === 0 && <p className="empty">Nothing has been posted yet.</p>}
        <ol className="journal-list">
          {shown.map((transaction) => (
            <li
              key={transaction.id}
              className={
                transaction.reversesTransactionId === null ? 'entry' : 'entry entry-reversal'
              }
            >
              <div className="entry-head">
                <h3>{titleOf(transaction, transactions)}</h3>
                <time dateTime={transaction.postedAt}>{timeOf(transaction.postedAt)}</time>
              </div>
              <table>
                <thead>
                  <tr>
                    <th scope="col">Account</th>
                    <th scope="col" className="num">
                      Debit
                    </th>
                    <th scope="col" className="num">
                      Credit
                    </th>
                    <th scope="col" className="num">
                      After
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {transaction.entries.map((entry) => {
                    const amount = formatMoney(entry.amount, transaction.currency);
                    return (
                      <tr key={entry.id}>
                        <th scope="row">{nameOf(accounts, entry.accountId)}</th>
                        <td className="num amount">{entry.direction === 'debit' ? amount : '—'}</td>
                        <td className="num amount">
                          {entry.direction === 'credit' ? amount : '—'}
                        </td>
                        <td className="num amount after">
                          {formatMoney(entry.balanceAfter, transaction.currency)}
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </li>
          ))}
        </ol>
        {transactions.length > SHOWN && (
          <p className="empty">
            Showing the latest {SHOWN} of {transactions.length} transactions.
          </p>
        )}
      </div>
    </Panel>
  );
}
