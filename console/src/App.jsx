import { useCallback, useMemo, useState, useSyncExternalStore } from 'react';
import { checkBooks } from './actions.js';
import { Accounts } from './components/Accounts.jsx';
import { BreakIt } from './components/BreakIt.jsx';
import { Composer } from './components/Composer.jsx';
import { EventFeed } from './components/EventFeed.jsx';
import { Journal } from './components/Journal.jsx';
import { Outcome } from './components/Outcome.jsx';
import { Payments } from './components/Payments.jsx';
import { TopBar } from './components/TopBar.jsx';
import { Tour } from './components/Tour.jsx';
import { readFeed } from './ledger/view.js';
import { describeBooks } from './outcome.js';

const REPOSITORY = 'https://github.com/ktodoran/neon-ledger';

export function App({ store }) {
  const snapshot = useSyncExternalStore(store.subscribe, store.getSnapshot);
  const [busy, setBusy] = useState(false);
  const [books, setBooks] = useState(null);
  // A new ledger gets new panels: forms and outcomes from the old one are dropped.
  const [generation, setGeneration] = useState(0);
  const [tourRun, setTourRun] = useState(0);

  /** Runs one piece of work at a time, with the buttons disabled while it does. */
  const act = useCallback(async (work) => {
    setBusy(true);
    try {
      return await work();
    } finally {
      setBusy(false);
    }
  }, []);

  const { transactions, payments } = useMemo(() => readFeed(snapshot.events), [snapshot.events]);
  // Customers first, then the settlement account they all move money through.
  const accounts = useMemo(
    () => [...snapshot.accounts].sort((a, b) => Number(a.allowNegative) - Number(b.allowNegative)),
    [snapshot.accounts],
  );

  async function check() {
    const result = await act(() => store.run(checkBooks()));
    setBooks(describeBooks(result.body));
  }

  /** Starts a new ledger. Returns the seed accounts' ids, which the tour needs. */
  const reset = useCallback(async () => {
    const cast = await act(() => store.reset());
    setBooks(null);
    setGeneration((current) => current + 1);
    return cast;
  }, [act, store]);

  async function startOver() {
    await reset();
    setTourRun((current) => current + 1);
  }

  if (!snapshot.ready) {
    return (
      <p className="loading" role="status">
        Opening the ledger…
      </p>
    );
  }

  return (
    <div className="app">
      <TopBar snapshot={snapshot} busy={busy} onCheck={check} onReset={startOver} />
      {books && (
        <div className="books-report">
          <Outcome outcome={books} />
          <button type="button" className="btn btn-quiet btn-small" onClick={() => setBooks(null)}>
            Dismiss
          </button>
        </div>
      )}

      <main className="grid">
        <div className="col col-accounts">
          <Accounts
            key={generation}
            accounts={accounts}
            busy={busy}
            onOpen={(request) => act(() => store.run((ledger) => ledger.openAccount(request)))}
          />
        </div>
        <div className="col col-work">
          <Tour key={tourRun} store={store} busy={busy} act={act} onStart={reset} />
          <Composer
            key={`composer-${String(generation)}`}
            store={store}
            accounts={accounts}
            busy={busy}
            act={act}
          />
          <Payments
            key={`payments-${String(generation)}`}
            store={store}
            accounts={accounts}
            payments={payments}
            busy={busy}
            act={act}
          />
          <BreakIt
            key={`breakit-${String(generation)}`}
            store={store}
            snapshot={snapshot}
            busy={busy}
            act={act}
          />
        </div>
        <div className="col col-record">
          <Journal accounts={accounts} transactions={transactions} />
          <EventFeed events={snapshot.events} transactions={transactions} />
        </div>
      </main>

      <footer className="foot">
        <p>
          This page runs a simulation of the ledger, written in JavaScript, in your browser. Nothing
          leaves it and nothing is saved. The real Neon Ledger is a .NET service on PostgreSQL:{' '}
          <a href={REPOSITORY}>source on GitHub</a>.
        </p>
      </footer>
    </div>
  );
}
