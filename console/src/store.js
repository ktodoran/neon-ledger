// What the console knows about the ledger, and the one way it changes.
//
// Every request goes through run(), which then reads the accounts and the
// events since the last cursor. The console never updates a balance itself:
// what is on screen is always what the ledger said.
//
// The ledger is either a simulation in this tab or a real one on this machine.
// They differ in two ways the store has to know about. The simulation's
// integrity check is free, so it runs after every request; the real one reads
// every entry, so it runs when asked. And a real ledger has other callers, so
// while connected the store reads it again every few seconds.

import { createHttpLedger } from './ledger/http.js';
import { seed } from './ledger/seed.js';
import { createSimulatedLedger } from './ledger/sim.js';

const POLL_MS = 4000;

const EMPTY = {
  ready: false,
  mode: 'simulated',
  address: null,
  // False when the last read of a connected ledger failed, and why: 0 for no
  // answer, 401 for a key the ledger no longer accepts.
  answering: true,
  trouble: null,
  accounts: [],
  events: [],
  cursor: null,
  books: null,
  booksCheckedAt: null,
};

export function createConsoleStore({
  createLedger = createSimulatedLedger,
  connectTo = createHttpLedger,
  now = () => new Date(),
} = {}) {
  const listeners = new Set();
  let ledger = null;
  let snapshot = EMPTY;
  let poll = null;
  // Reads run one at a time, so two of them cannot both append the same events.
  let reading = Promise.resolve();

  function publish(next) {
    snapshot = next;
    for (const listener of listeners) listener();
  }

  async function read(from, base) {
    const simulated = from.mode === 'simulated';
    const [accounts, page, books] = await Promise.all([
      from.listAccounts(),
      from.events(base.cursor),
      simulated ? from.integrity() : null,
    ]);
    // A reset or a connection made while this read was in the air wins.
    if (from !== ledger) return;
    if (accounts.status !== 200 || page.status !== 200) {
      publish({
        ...base,
        ready: true,
        mode: from.mode,
        address: from.address ?? null,
        answering: false,
        trouble: accounts.status !== 200 ? accounts.status : page.status,
      });
      return;
    }
    publish({
      ...base,
      ready: true,
      mode: from.mode,
      address: from.address ?? null,
      answering: true,
      trouble: null,
      accounts: accounts.body,
      events: [...base.events, ...page.body.items],
      cursor: page.body.nextCursor,
      ...(simulated ? { books: books.body, booksCheckedAt: now().toISOString() } : {}),
    });
  }

  /** Reads the ledger and publishes one new snapshot. */
  function refresh(base) {
    const from = ledger;
    reading = reading.then(() => read(from, base ?? snapshot));
    return reading;
  }

  function switchTo(next) {
    clearInterval(poll);
    ledger = next;
    poll = next.mode === 'connected' ? setInterval(() => void refresh(), POLL_MS) : null;
    return refresh(EMPTY);
  }

  return {
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },

    getSnapshot: () => snapshot,

    /** Starts a new simulation with the seed accounts, and returns their ids by role. */
    async reset() {
      // The old ledger stays on screen until the new one is ready to replace it.
      const next = createLedger();
      const cast = await seed(next);
      await switchTo(next);
      return cast;
    },

    /**
     * Gives the tour its accounts. A simulation starts over; a real ledger gets
     * three new accounts and keeps everything it had. Null if that failed.
     */
    async begin() {
      if (ledger.mode === 'simulated') return this.reset();
      const cast = await seed(ledger, snapshot.accounts);
      await refresh();
      return cast;
    },

    /**
     * Switches to the ledger at `address` if it answers and accepts the key.
     * Otherwise changes nothing. The key is kept by the ledger client and
     * nowhere else: not in the snapshot, the address bar or browser storage.
     */
    async connect(address, key) {
      const next = connectTo(address, { key });
      const health = await next.health();
      if (health.status !== 200) return { connected: false, status: health.status };
      // The health check is open to anyone. Reading is what shows the key is accepted.
      const accounts = await next.listAccounts();
      if (accounts.status !== 200) return { connected: false, status: accounts.status };
      await switchTo(next);
      return { connected: true };
    },

    /** Sends requests to the ledger, then brings the console up to date. */
    async run(request) {
      const result = await request(ledger);
      await refresh();
      return result;
    },

    /** Asks the ledger to recompute its balances and holds, and keeps the report. */
    async checkBooks() {
      const from = ledger;
      const result = await from.integrity();
      if (result.status === 200 && from === ledger) {
        publish({ ...snapshot, books: result.body, booksCheckedAt: now().toISOString() });
      }
      return result;
    },

    /** Stops reading a connected ledger. For a console that is going away. */
    stop: () => clearInterval(poll),
  };
}
