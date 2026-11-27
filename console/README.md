# Neon Ledger console

A browser console for the ledger: accounts, a form for moving money, the payments and their lifecycle, the journal, the event feed, and a six-step tour that checks the ledger's main claims one at a time.

React and Vite, in JavaScript. No UI library and no state library.

```bash
npm install
npm run dev      # http://localhost:5173
```

## What it runs against

Today, a simulation. `src/ledger/sim.js` is the ledger's rules written again in JavaScript, answering the same requests as the API with the same response shapes, status codes and refusal codes. It lives in the browser tab: nothing is sent anywhere, and a reload starts over.

That means the console shows what the ledger does, not that the real one does it. The evidence for the real one is its own test suite, in `tests/`. Two things the simulation cannot show at all:

- **Concurrency.** It handles requests one at a time. The "race" trial reaches the outcome the real ledger reaches, one withdrawal posted and one refused, but not by the same mechanism, and the console says so when it reports the result.
- **The database.** The real ledger also enforces its rules in PostgreSQL, with constraints and triggers. There is no second line of defense here.

Connecting the console to a running ledger is the next piece of work. The console already talks to the ledger through one small interface (`listAccounts`, `postTransaction`, `createPayment`, and so on), so that is a second implementation of it, not a rewrite.

## How it is put together

| Path                 | What it holds                                                                                                        |
| -------------------- | -------------------------------------------------------------------------------------------------------------------- |
| `src/ledger/sim.js`  | The simulated ledger.                                                                                                |
| `src/ledger/view.js` | Builds the journal and the payments table from the event feed.                                                       |
| `src/store.js`       | The one place requests are sent from. After each one it reads the accounts, the new events and the integrity report. |
| `src/actions.js`     | The requests the console sends.                                                                                      |
| `src/outcome.js`     | Turns a response into the status line and sentence shown on screen.                                                  |
| `src/tour.js`        | The six tour steps.                                                                                                  |
| `src/components/`    | One file per panel.                                                                                                  |

Two decisions worth knowing about:

- **The console never computes a balance.** Every number on screen came back from the ledger. The journal and the payments table are read from the event feed, the same feed another system would use to stay in step.
- **The tour sends real requests.** Each step reports what the ledger answered. If a visitor cancels the tour's payment behind its back, the next step shows the refusal, not the sentence it expected to show.

## Checks

```bash
npm test             # the simulation's rules, and the console driven through its buttons
npm run lint
npm run format:check
npm run build
```

## Known limits

- Amounts are shown in US dollars, and the accounts the console opens are USD.
- A returned payment is always returned as `R01`.
- The journal shows the latest 40 transactions and the feed the latest 60 events.
- Checked in Chrome only.
