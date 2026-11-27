// What the console knows about the ledger, and the one way it changes.
//
// Every request goes through run(), which then reads the accounts, the events
// since the last cursor, and the integrity report. The console never updates a
// balance itself: what is on screen is always what the ledger said.

import { seed } from './ledger/seed.js';
import { createSimulatedLedger } from './ledger/sim.js';

const EMPTY = {
  ready: false,
  mode: 'simulated',
  accounts: [],
  events: [],
  cursor: '0',
  books: null,
};

export function createConsoleStore({ createLedger = createSimulatedLedger } = {}) {
  const listeners = new Set();
  let ledger = null;
  let snapshot = EMPTY;

  function publish(next) {
    snapshot = next;
    for (const listener of listeners) listener();
  }

  /** Reads the ledger and publishes one new snapshot, built on `base`. */
  async function refresh(base = snapshot) {
    const [accounts, page, books] = await Promise.all([
      ledger.listAccounts(),
      ledger.events(base.cursor),
      ledger.integrity(),
    ]);
    publish({
      ready: true,
      mode: ledger.mode,
      accounts: accounts.body,
      events: [...base.events, ...page.body.items],
      cursor: page.body.nextCursor,
      books: books.body,
    });
  }

  return {
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },

    getSnapshot: () => snapshot,

    /** Starts a new ledger with the seed accounts, and returns their ids by role. */
    async reset() {
      // The old ledger stays on screen until the new one is ready to replace it.
      const next = createLedger();
      const cast = await seed(next);
      ledger = next;
      await refresh(EMPTY);
      return cast;
    },

    /** Sends requests to the ledger, then brings the console up to date. */
    async run(request) {
      const result = await request(ledger);
      await refresh();
      return result;
    },
  };
}
