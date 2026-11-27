import { useId, useState } from 'react';
import { move, pay } from '../actions.js';
import { newKey } from '../lib/keys.js';
import { formatMoney, parseMoney } from '../lib/money.js';
import { describePayment, nameOf, outcomeOf, succeeded } from '../outcome.js';
import { Outcome } from './Outcome.jsx';
import { Panel } from './Panel.jsx';

const KINDS = {
  deposit: {
    label: 'Deposit',
    memo: 'Paycheck',
    send: 'Post deposit',
    hint: 'A transaction of two entries: debit the settlement account, credit the customer.',
  },
  transfer: {
    label: 'Transfer',
    memo: 'Savings transfer',
    send: 'Post transfer',
    hint: 'A transaction between two accounts, posted at once.',
  },
  payment: {
    label: 'Payment',
    memo: 'Electric bill',
    send: 'Create payment',
    hint: 'A payment has a lifecycle. On ACH the money is held until it settles.',
  },
};

const RAILS = { ach: 'ACH · settles later', instant: 'Instant' };

/**
 * The accounts a kind of movement starts from and lands in, until the form is
 * told otherwise. Money leaves the customer who has the most to spend, so the
 * first thing a visitor sends is not refused for want of funds, and it lands
 * in an account of the same currency, so it is not refused for that either.
 */
function defaults(kind, accounts) {
  const settlements = accounts.filter((account) => account.allowNegative);
  const customers = accounts.filter((account) => !account.allowNegative);
  const matching = (candidates, from, except) =>
    candidates.find((account) => account !== except && account.currency === from?.currency) ??
    candidates.find((account) => account !== except);

  if (kind === 'deposit') {
    const from = settlements[0];
    return { from: from?.id, to: matching(customers, from)?.id };
  }
  const richest = customers.reduce(
    (best, account) => (best === undefined || account.available > best.available ? account : best),
    undefined,
  );
  const to =
    kind === 'transfer'
      ? (matching(customers, richest, richest) ?? matching(settlements, richest))
      : matching(settlements, richest);
  return { from: richest?.id, to: to?.id };
}

function Segments({ legend, name, options, value, onChange }) {
  return (
    <fieldset className="segments">
      <legend>{legend}</legend>
      {Object.entries(options).map(([option, label]) => (
        <label key={option} className="segment">
          <input
            type="radio"
            name={name}
            value={option}
            checked={value === option}
            onChange={() => onChange(option)}
          />
          <span>{label}</span>
        </label>
      ))}
    </fieldset>
  );
}

function AccountSelect({ label, value, accounts, onChange }) {
  const id = useId();
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <select id={id} value={value ?? ''} onChange={(event) => onChange(event.target.value)}>
        {accounts.map((account) => (
          <option key={account.id} value={account.id}>
            {account.name} · {formatMoney(account.available, account.currency)}
          </option>
        ))}
      </select>
    </div>
  );
}

export function Composer({ store, accounts, busy, act }) {
  const ids = { amount: useId(), memo: useId(), key: useId() };
  const [form, setForm] = useState({
    kind: 'deposit',
    from: null,
    to: null,
    amount: '250.00',
    rail: 'ach',
    memo: KINDS.deposit.memo,
    key: newKey('deposit'),
  });
  // The last request sent and its key, kept so it can be sent again unchanged.
  const [sent, setSent] = useState(null);
  const [error, setError] = useState(null);
  const [outcome, setOutcome] = useState(null);

  const fallback = defaults(form.kind, accounts);
  const known = (id) => accounts.some((account) => account.id === id);
  const from = known(form.from) ? form.from : fallback.from;
  const to = known(form.to) ? form.to : fallback.to;

  // A change to the request is a new request, and a new request needs a new key.
  function change(patch) {
    setForm((current) => ({
      ...current,
      ...patch,
      key: sent === null ? current.key : newKey(patch.kind ?? current.kind),
    }));
    setSent(null);
    setError(null);
  }

  function switchKind(kind) {
    setForm((current) => ({
      ...current,
      kind,
      from: null,
      to: null,
      memo: KINDS[kind].memo,
      key: newKey(kind),
    }));
    setSent(null);
    setError(null);
  }

  async function send(request, key) {
    const result = await act(() => store.run(request(key)));
    const after = store.getSnapshot().accounts;
    if (form.kind === 'payment' && succeeded(result)) {
      const { tone, message } = describePayment(result.body, after);
      setOutcome(outcomeOf(result, after, message, tone));
    } else {
      const amount = formatMoney(result.body?.entries?.[0]?.amount ?? 0);
      setOutcome(
        outcomeOf(
          result,
          after,
          `Posted. Two entries, ${amount} on each side: a debit on ${nameOf(after, from)} and a credit on ${nameOf(after, to)}.`,
        ),
      );
    }
  }

  async function submit(event) {
    event.preventDefault();
    const amount = parseMoney(form.amount);
    if (amount === null) {
      setError('Enter an amount such as 120.00.');
      return;
    }
    if (from === to) {
      setError('Choose two different accounts.');
      return;
    }
    const details = { from, to, amount, memo: form.memo, rail: form.rail };
    const request = (key) => (form.kind === 'payment' ? pay(details, key) : move(details, key));
    setSent({ request, key: form.key });
    await send(request, form.key);
  }

  const deposit = defaults('deposit', accounts);
  if (deposit.from === undefined || deposit.to === undefined) {
    return (
      <Panel title="Move money" className="composer">
        <p className="empty">
          Moving money takes a settlement account and a customer account. Open them on the left, or
          start the tour, which opens its own.
        </p>
      </Panel>
    );
  }

  return (
    <Panel title="Move money" className="composer">
      <form onSubmit={submit} noValidate>
        <Segments
          legend="Kind of movement"
          name="kind"
          options={Object.fromEntries(
            Object.entries(KINDS).map(([kind, { label }]) => [kind, label]),
          )}
          value={form.kind}
          onChange={switchKind}
        />
        <p className="hint">{KINDS[form.kind].hint}</p>

        <div className="fields">
          <AccountSelect
            label="From"
            value={from}
            accounts={accounts}
            onChange={(id) => change({ from: id })}
          />
          <AccountSelect
            label="To"
            value={to}
            accounts={accounts}
            onChange={(id) => change({ to: id })}
          />
          <div className="field">
            <label htmlFor={ids.amount}>Amount</label>
            <div className="money-input">
              <span aria-hidden="true">$</span>
              <input
                id={ids.amount}
                className="amount"
                inputMode="decimal"
                autoComplete="off"
                value={form.amount}
                onChange={(event) => change({ amount: event.target.value })}
                aria-invalid={error !== null}
                aria-describedby={error ? `${ids.amount}-error` : undefined}
              />
            </div>
          </div>
          <div className="field">
            <label htmlFor={ids.memo}>Memo</label>
            <input
              id={ids.memo}
              value={form.memo}
              maxLength={255}
              autoComplete="off"
              onChange={(event) => change({ memo: event.target.value })}
            />
          </div>
          {form.kind === 'payment' && (
            <Segments
              legend="Rail"
              name="rail"
              options={RAILS}
              value={form.rail}
              onChange={(rail) => change({ rail })}
            />
          )}
          <div className="field">
            <label htmlFor={ids.key}>Idempotency key</label>
            <input
              id={ids.key}
              className="mono"
              value={form.key}
              maxLength={255}
              autoComplete="off"
              spellCheck="false"
              onChange={(event) => {
                setForm((current) => ({ ...current, key: event.target.value }));
                setSent(null);
              }}
            />
          </div>
        </div>

        {error && (
          <p className="field-error" id={`${ids.amount}-error`} role="alert">
            {error}
          </p>
        )}

        <div className="row">
          <button type="submit" className="btn btn-primary" disabled={busy}>
            {KINDS[form.kind].send}
          </button>
          <button
            type="button"
            className="btn btn-outline"
            disabled={busy || sent === null}
            onClick={() => send(sent.request, sent.key)}
          >
            Send it again with the same key
          </button>
        </div>
      </form>
      <Outcome outcome={outcome} />
    </Panel>
  );
}
