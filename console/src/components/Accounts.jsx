import { useId, useState } from 'react';
import { formatMoney } from '../lib/money.js';
import { Panel } from './Panel.jsx';

const KINDS = {
  customer: { label: 'Customer', normalBalance: 'credit', allowNegative: false },
  settlement: { label: 'Settlement', normalBalance: 'debit', allowNegative: true },
};

function AccountCard({ account }) {
  const money = (amount) => formatMoney(amount, account.currency);
  const heldShare =
    account.balance > 0 ? Math.min(100, Math.round((account.held / account.balance) * 100)) : 0;
  return (
    <li className="account">
      <div className="account-head">
        <h3>{account.name}</h3>
        <span className="tag">{account.allowNegative ? 'Settlement' : 'Customer'}</span>
      </div>
      <dl className="account-figures">
        <div className="figure figure-main">
          <dt>Available</dt>
          <dd className="amount">{money(account.available)}</dd>
        </div>
        <div className="figure">
          <dt>Balance</dt>
          <dd className="amount">{money(account.balance)}</dd>
        </div>
        <div className="figure">
          <dt>Held</dt>
          <dd className={account.held > 0 ? 'amount amount-held' : 'amount'}>
            {money(account.held)}
          </dd>
        </div>
      </dl>
      {account.held > 0 && (
        <>
          <div className="heldbar" aria-hidden="true">
            <span style={{ width: `${String(heldShare)}%` }} />
          </div>
          <p className="account-note">{money(account.held)} is set aside for pending payments.</p>
        </>
      )}
      {account.allowNegative && (
        <p className="account-note">Where money enters and leaves the ledger. May go negative.</p>
      )}
    </li>
  );
}

function OpenAccount({ busy, onOpen, onClose }) {
  const nameId = useId();
  const [name, setName] = useState('');
  const [kind, setKind] = useState('customer');
  const [error, setError] = useState(null);

  async function submit(event) {
    event.preventDefault();
    const { normalBalance, allowNegative } = KINDS[kind];
    const result = await onOpen({ name, currency: 'USD', normalBalance, allowNegative });
    if (result.status === 201) onClose();
    else setError(Object.values(result.body.errors ?? {}).flat()[0] ?? result.body.detail);
  }

  return (
    <form className="open-account" onSubmit={submit} noValidate>
      <div className="field">
        <label htmlFor={nameId}>Account name</label>
        <input
          id={nameId}
          value={name}
          onChange={(event) => setName(event.target.value)}
          placeholder="Sam checking"
          autoComplete="off"
          maxLength={120}
          aria-invalid={error !== null}
          aria-describedby={error ? `${nameId}-error` : undefined}
          autoFocus
        />
        {error && (
          <p className="field-error" id={`${nameId}-error`}>
            {error}
          </p>
        )}
      </div>
      <fieldset className="segments">
        <legend>Kind</legend>
        {Object.entries(KINDS).map(([value, { label }]) => (
          <label key={value} className="segment">
            <input
              type="radio"
              name="account-kind"
              value={value}
              checked={kind === value}
              onChange={() => setKind(value)}
            />
            <span>{label}</span>
          </label>
        ))}
      </fieldset>
      <p className="hint">
        {kind === 'customer'
          ? 'Holds a customer’s money. It can never go below zero.'
          : 'Stands in for the outside world. It may go negative.'}
      </p>
      <div className="row">
        <button type="submit" className="btn btn-primary" disabled={busy}>
          Open account
        </button>
        <button type="button" className="btn btn-quiet" onClick={onClose}>
          Cancel
        </button>
      </div>
    </form>
  );
}

export function Accounts({ accounts, busy, onOpen }) {
  const [opening, setOpening] = useState(false);
  return (
    <Panel
      title="Accounts"
      className="accounts"
      action={
        !opening && (
          <button
            type="button"
            className="btn btn-outline btn-small"
            onClick={() => setOpening(true)}
          >
            Open account
          </button>
        )
      }
    >
      {opening && <OpenAccount busy={busy} onOpen={onOpen} onClose={() => setOpening(false)} />}
      <ul className="account-list">
        {accounts.map((account) => (
          <AccountCard key={account.id} account={account} />
        ))}
      </ul>
    </Panel>
  );
}
