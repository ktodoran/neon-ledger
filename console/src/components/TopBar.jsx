import { formatMoney } from '../lib/money.js';
import { timeOf } from './Journal.jsx';

function Books({ snapshot }) {
  const { books, booksCheckedAt, mode } = snapshot;
  if (books === null) {
    return <p className="books-state books-unknown">Books not checked yet</p>;
  }
  const state = books.consistent ? 'Books balance' : 'Books do not balance';
  return (
    <p className={books.consistent ? 'books-state' : 'books-state books-off'}>
      <span className="dot" aria-hidden="true" />
      {/* A simulation is checked after every request. A real ledger is checked when asked. */}
      {mode === 'simulated' ? state : `${state}, checked at ${timeOf(booksCheckedAt)}`}: debits
      minus credits is <strong className="amount">{formatMoney(books.debitsMinusCredits)}</strong>
    </p>
  );
}

export function TopBar({ snapshot, busy, onCheck, onReset, onConnect, onDisconnect }) {
  const connected = snapshot.mode === 'connected';
  const host = connected ? new URL(snapshot.address).host : null;
  return (
    <header className="topbar">
      <div className="brand">
        <svg className="brand-mark" viewBox="0 0 32 32" aria-hidden="true">
          <rect width="32" height="32" rx="7" />
          <path d="M9 8v16h14" />
        </svg>
        <h1>
          Neon Ledger <span>console</span>
        </h1>
      </div>

      {connected ? (
        <div className="mode">
          <span className={snapshot.answering ? 'chip chip-good' : 'chip chip-warn'}>
            <span className="dot" aria-hidden="true" />
            {snapshot.answering ? `Connected to ${host}` : `No answer from ${host}`}
          </span>
          <button type="button" className="btn btn-quiet" onClick={onDisconnect} disabled={busy}>
            Disconnect
          </button>
        </div>
      ) : (
        <div className="mode">
          <span className="chip chip-mode">Simulated in this browser</span>
          <button type="button" className="btn btn-quiet" onClick={onConnect} disabled={busy}>
            Connect a real ledger
          </button>
          <button type="button" className="btn btn-quiet" onClick={onReset} disabled={busy}>
            Start over
          </button>
        </div>
      )}

      <div className="books">
        <Books snapshot={snapshot} />
        <button type="button" className="btn btn-outline" onClick={onCheck} disabled={busy}>
          Check the books
        </button>
      </div>
    </header>
  );
}
