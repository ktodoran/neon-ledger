# Neon Ledger console

A browser console for the ledger: accounts, a form for moving money, the payments and their lifecycle, the journal, the event feed, and a six-step tour that checks the ledger's main claims one at a time.

React and Vite, in JavaScript. No UI library and no state library. Published at [ktodoran.github.io/neon-ledger](https://ktodoran.github.io/neon-ledger/).

```bash
npm install
npm run dev      # http://localhost:5173
```

## What it runs against

**A simulation, by default.** `src/ledger/sim.js` is the ledger's rules written again in JavaScript, answering the same requests as the API with the same response shapes, status codes and refusal codes. It lives in the browser tab: nothing is sent anywhere, and a reload starts over.

**A real ledger, when connected.** "Connect a real ledger" in the top bar switches the console to a Neon Ledger running on your machine. Start one from the repository root and create a key for the console, then connect to `http://localhost:5080` with that key:

```bash
docker compose up -d --wait
dotnet run --project src/NeonLedger.Api -- migrate
dotnet run --project src/NeonLedger.Api -- keys create console    # prints the key, once
dotnet run --project src/NeonLedger.Api
```

`http://localhost:5173/?ledger=http://localhost:5080` opens the connect panel with the address filled in. The key is always typed: it does not belong in a link.

The console keeps the key in memory for as long as the page is open. It is not written to the address bar, to browser storage or anywhere on the page, and a reload forgets it.

When connected:

- The console shows what that ledger holds: every account, and a journal and payments table built from its whole event feed. It reads the ledger again every four seconds, so a request sent with `curl` shows up too.
- The tour opens three new accounts for itself and leaves the others alone.
- The "race" trial sends two withdrawals at the same moment, and the ledger's row lock decides which one loses.
- The integrity check runs when you press "Check the books", not after every request. On a real ledger it reads every entry.

Only an address on the same machine is accepted (`localhost`, `127.0.0.1` or `[::1]`), so the key is never sent anywhere else. A read-only key connects and shows everything; every button that writes is answered with the ledger's `403`.

### From the public page

The published console is served over https and a local ledger over http. Browsers treat that differently:

- **Chrome and Edge** allow it after asking for permission to reach devices on your local network. A connection attempted before that permission is granted fails; connect again once it is.
- **Safari** refuses to call a local http address from an https page.

Running the console locally (`npm run dev`) avoids both, since page and ledger are then both on `localhost`.

The ledger also has to accept the page's origin. Its development configuration lists the three the console uses (see "Calling it from a web page" in the main README).

## How the simulation is kept honest

The simulation shows what the ledger does. It is not evidence that the real one does it: that is the ledger's own test suite, in `tests/`. What ties the two together is `npm run parity`:

```bash
NEON_LEDGER_KEY=nl_... npm run parity                          # against http://localhost:5080
NEON_LEDGER_KEY=nl_... npm run parity -- http://localhost:5081
```

The key has to be one that may write.

It runs one script (`src/ledger/script.js`: 35 requests covering deposits, replays, key reuse, validation, overdrafts, holds, and every payment transition, allowed and refused) against the simulation and against the real API, replaces the things that differ by nature (ids, times, cursors), and reports every other difference. CI runs it on every push.

Three things the simulation still cannot show, and the console says so where it matters:

- **Callers.** It has no keys and one caller. Authentication is the real ledger's, and is tested there.
- **Concurrency.** It handles requests one at a time. Its "race" reaches the outcome the real ledger reaches, but not by the same mechanism.
- **The database.** The real ledger also enforces its rules in PostgreSQL, with constraints and triggers. The simulation has no second line of defense.

## How it is put together

| Path                   | What it holds                                                                                  |
| ---------------------- | ---------------------------------------------------------------------------------------------- |
| `src/ledger/sim.js`    | The simulated ledger.                                                                          |
| `src/ledger/http.js`   | The same interface over HTTP, for a real ledger.                                               |
| `src/ledger/script.js` | The script both are run against.                                                               |
| `src/ledger/view.js`   | Builds the journal and the payments table from the event feed.                                 |
| `src/store.js`         | The one place requests are sent from. After each one it reads the accounts and the new events. |
| `src/actions.js`       | The requests the console sends.                                                                |
| `src/outcome.js`       | Turns a response into the status line and sentence shown on screen.                            |
| `src/tour.js`          | The six tour steps.                                                                            |
| `src/components/`      | One file per panel.                                                                            |

Three decisions worth knowing about:

- **One interface, two ledgers.** Everything above `src/ledger/` talks to an object with `listAccounts`, `postTransaction`, `createPayment` and so on, and does not know which kind it has. A request the browser could not deliver comes back as an answer with status 0, not an exception, so there is one way to show a failure.
- **The console never computes a balance.** Every number on screen came back from the ledger. The journal and the payments table are read from the event feed, the same feed another system would use to stay in step.
- **The tour sends real requests.** Each step reports what the ledger answered. If a visitor cancels the tour's payment behind its back, the next step shows the refusal, not the sentence it expected to show.

## Checks

```bash
npm test             # the simulation's rules, the HTTP client, and the console driven through its buttons
npm run lint
npm run format:check
npm run build
npm run parity       # needs a running ledger
```

## Known limits

- Connected, the console reads the whole event feed and every account. That suits a ledger on your machine, not a busy one.
- Amounts in the forms are US dollars, and the accounts the console opens are USD.
- A returned payment is always returned as `R01`.
- The journal shows the latest 40 transactions and the feed the latest 60 events.
- Checked in Chrome only.
