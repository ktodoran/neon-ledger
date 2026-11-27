import { formatMoney } from '../lib/money.js';

export function TopBar({ snapshot, busy, onCheck, onReset }) {
  const { books } = snapshot;
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

      <div className="mode">
        <span className="chip chip-mode">Simulated in this browser</span>
        <button type="button" className="btn btn-quiet" onClick={onReset} disabled={busy}>
          Start over
        </button>
      </div>

      <div className="books">
        {books && (
          <p className={books.consistent ? 'books-state' : 'books-state books-off'}>
            <span className="dot" aria-hidden="true" />
            {books.consistent ? 'Books balance' : 'Books do not balance'}: debits minus credits is{' '}
            <strong className="amount">{formatMoney(books.debitsMinusCredits)}</strong>
          </p>
        )}
        <button type="button" className="btn btn-outline" onClick={onCheck} disabled={busy}>
          Check the books
        </button>
      </div>
    </header>
  );
}
