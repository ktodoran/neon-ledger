import { useCallback, useMemo, useState, useSyncExternalStore } from 'react';
import { Accounts } from './components/Accounts.jsx';
import { BreakIt } from './components/BreakIt.jsx';
import { Composer } from './components/Composer.jsx';
import { Connect } from './components/Connect.jsx';
import { EventFeed } from './components/EventFeed.jsx';
import { Journal } from './components/Journal.jsx';
import { Outcome } from './components/Outcome.jsx';
import { Payments } from './components/Payments.jsx';
import { TopBar } from './components/TopBar.jsx';
import { Tour } from './components/Tour.jsx';
import { readFeed } from './ledger/view.js';
import { describeBooks, outcomeOf, succeeded } from './outcome.js';

const REPOSITORY = 'https://github.com/ktodoran/neon-ledger';

/**
 * `ledgerAddress` is set when the page was opened with ?ledger=: the connect
 * panel opens with that address filled in, waiting for a key.
 */
export function App({ store, ledgerAddress = null }) {
  const snapshot = useSyncExternalStore(store.subscribe, store.getSnapshot);
  const [busy, setBusy] = useState(false);
  const [books, setBooks] = useState(null);
  const [connecting, setConnecting] = useState(ledgerAddress !== null);
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
  // Customers first, then the settlement accounts they move money through, and
  // the newest of each at the top. The ledger lists accounts oldest first.
  const accounts = useMemo(
    () =>
      [...snapshot.accounts]
        .reverse()
        .sort((a, b) => Number(a.allowNegative) - Number(b.allowNegative)),
    [snapshot.accounts],
  );

  const fresh = useCallback(() => {
    setBooks(null);
    setGeneration((current) => current + 1);
  }, []);

  async function check() {
    const result = await act(() => store.checkBooks());
    setBooks(
      succeeded(result) ? describeBooks(result.body) : outcomeOf(result, snapshot.accounts, ''),
    );
  }

  /** Gives the tour its accounts and clears the panels. Null if the ledger refused. */
  const beginTour = useCallback(async () => {
    const cast = await act(() => store.begin());
    fresh();
    return cast;
  }, [act, fresh, store]);

  /** A new simulation, whether the last ledger was one or not. */
  async function startOver() {
    await act(() => store.reset());
    fresh();
    setTourRun((current) => current + 1);
  }

  async function connect(address, key) {
    const result = await act(() => store.connect(address, key));
    if (result.connected) {
      setConnecting(false);
      fresh();
      setTourRun((current) => current + 1);
    }
    return result;
  }

  if (!snapshot.ready) {
    return (
      <p className="loading" role="status">
        Opening the ledger…
      </p>
    );
  }

  const connected = snapshot.mode === 'connected';

  return (
    <div className="app">
      <TopBar
        snapshot={snapshot}
        busy={busy}
        onCheck={check}
        onReset={startOver}
        onConnect={() => setConnecting(true)}
        onDisconnect={startOver}
      />
      {connecting && !connected && (
        <Connect
          busy={busy}
          initialAddress={ledgerAddress}
          onConnect={connect}
          onClose={() => setConnecting(false)}
        />
      )}
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
          <Tour
            key={tourRun}
            store={store}
            mode={snapshot.mode}
            busy={busy}
            act={act}
            onStart={beginTour}
          />
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
            accounts={accounts}
            mode={snapshot.mode}
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
        {connected ? (
          <p>
            This page is sending requests to the Neon Ledger at {snapshot.address}, a .NET service
            on PostgreSQL, and reading it again every few seconds. What you post stays in that
            ledger. <a href={REPOSITORY}>Source on GitHub</a>.
          </p>
        ) : (
          <p>
            This page runs a simulation of the ledger, written in JavaScript, in your browser.
            Nothing leaves it and nothing is saved. The real Neon Ledger is a .NET service on
            PostgreSQL, and the console can connect to one running on your machine:{' '}
            <a href={REPOSITORY}>source on GitHub</a>.
          </p>
        )}
      </footer>
    </div>
  );
}
