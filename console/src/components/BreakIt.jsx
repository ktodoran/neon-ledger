import { useState } from 'react';
import { move } from '../actions.js';
import { newKey } from '../lib/keys.js';
import { formatMoney } from '../lib/money.js';
import { outcomeOf, succeeded } from '../outcome.js';
import { Outcome } from './Outcome.jsx';
import { Panel } from './Panel.jsx';

const RETRY_AMOUNT = 1000;

/** An amount that one withdrawal can afford and two cannot. */
function raceAmount(available) {
  const most = Math.floor((available * 0.8) / 100) * 100;
  return most > 0 ? most : available;
}

export function BreakIt({ store, snapshot, busy, act }) {
  const [outcome, setOutcome] = useState(null);
  const { accounts, mode } = snapshot;
  const settlement = accounts.find((account) => account.allowNegative);
  const customers = accounts.filter((account) => !account.allowNegative);
  const richest = customers.reduce(
    (best, account) => (best === null || account.available > best.available ? account : best),
    null,
  );
  const usable = settlement !== undefined && customers.length > 0;
  const raceable = usable && richest.available > 0;
  const withdrawal = raceable ? raceAmount(richest.available) : 0;

  async function retry() {
    const target = customers[0];
    const key = newKey('retry');
    const request = move(
      { from: settlement.id, to: target.id, amount: RETRY_AMOUNT, memo: 'Retried deposit' },
      key,
    );
    const [first, second] = await act(async () => [
      await store.run(request),
      await store.run(request),
    ]);
    const after = store.getSnapshot().accounts;
    if (succeeded(first) && second.replayed === true) {
      setOutcome({
        label: '201 Created, then replayed',
        tone: 'good',
        message: `Sent twice with the key ${key}. The first posted ${formatMoney(RETRY_AMOUNT)} to ${target.name}. The second got the first response back, so the journal has one entry, not two.`,
      });
    } else {
      setOutcome(outcomeOf(second, after, 'The second request posted again.', 'warn'));
    }
  }

  async function race() {
    const details = {
      from: richest.id,
      to: settlement.id,
      amount: withdrawal,
      memo: 'Racing withdrawal',
    };
    const results = await act(() =>
      store.run((ledger) =>
        Promise.all([
          move(details, newKey('race-a'))(ledger),
          move(details, newKey('race-b'))(ledger),
        ]),
      ),
    );
    const after = store.getSnapshot().accounts;
    const posted = results.filter(succeeded).length;
    const refused = results.filter((result) => result.body?.code === 'insufficient_funds').length;
    const left = after.find((account) => account.id === richest.id).available;
    if (posted === 1 && refused === 1) {
      setOutcome({
        label: '1 posted · 1 refused',
        tone: 'good',
        message: `One ${formatMoney(withdrawal)} withdrawal posted. The other was refused with insufficient_funds, and ${richest.name} has ${formatMoney(left)} available. ${
          mode === 'simulated'
            ? 'This simulation handles the two one after the other. The real ledger reaches the same result by locking the account row.'
            : 'The ledger locked the account row, so the second request saw the first one’s result.'
        }`,
      });
    } else {
      setOutcome({
        label: `${String(posted)} posted · ${String(refused)} refused`,
        tone: 'warn',
        message: `Expected one of each. ${richest.name} has ${formatMoney(left)} available.`,
      });
    }
  }

  async function unbalanced() {
    const result = await act(() =>
      store.run((ledger) =>
        ledger.postTransaction(
          {
            description: 'Off by one cent',
            entries: [
              { accountId: settlement.id, direction: 'debit', amount: 1000 },
              { accountId: customers[0].id, direction: 'credit', amount: 999 },
            ],
          },
          newKey('unbalanced'),
        ),
      ),
    );
    const after = store.getSnapshot().accounts;
    setOutcome(
      outcomeOf(result, after, 'Posted, which should not be possible. Check the books.', 'warn'),
    );
  }

  return (
    <Panel
      title="Try to break it"
      note="Three things that go wrong in real payment systems, and what the ledger does about each."
      className="breakit"
    >
      <ul className="trials">
        <li className="trial">
          <h3>A retry</h3>
          <p>
            Send one {formatMoney(RETRY_AMOUNT)} deposit twice with the same key, as a client does
            after a timeout. It should act once.
          </p>
          <button
            type="button"
            className="btn btn-outline"
            onClick={retry}
            disabled={busy || !usable}
          >
            Send it twice
          </button>
        </li>
        <li className="trial">
          <h3>A race</h3>
          <p>
            {raceable
              ? `Two ${formatMoney(withdrawal)} withdrawals at the same moment, from ${richest.name} with ${formatMoney(richest.available)} available. One should lose.`
              : 'Two withdrawals at the same moment from one account. It needs an account with money available: make a deposit first.'}
          </p>
          <button
            type="button"
            className="btn btn-outline"
            onClick={race}
            disabled={busy || !raceable}
          >
            Race them
          </button>
        </li>
        <li className="trial">
          <h3>An unbalanced entry</h3>
          <p>
            Post a transaction whose debits and credits differ by one cent. It should be refused.
          </p>
          <button
            type="button"
            className="btn btn-outline"
            onClick={unbalanced}
            disabled={busy || !usable}
          >
            Post it
          </button>
        </li>
      </ul>
      <Outcome outcome={outcome} />
    </Panel>
  );
}
