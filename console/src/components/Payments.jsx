import { useState } from 'react';
import { cancel, giveBack, settle } from '../actions.js';
import { newKey } from '../lib/keys.js';
import { formatMoney } from '../lib/money.js';
import { describePayment, nameOf, outcomeOf, succeeded } from '../outcome.js';
import { Outcome } from './Outcome.jsx';
import { Panel } from './Panel.jsx';

// What the console sends when a payment is returned: the ACH code for
// insufficient funds, the most common reason a debit comes back.
const RETURN_REASON = 'R01';

const STATUS = {
  pending: { tone: 'held', label: () => 'Pending' },
  settled: { tone: 'good', label: () => 'Settled' },
  canceled: { tone: 'quiet', label: () => 'Canceled' },
  returned: { tone: 'warn', label: (payment) => `Returned · ${payment.returnReason}` },
};

export function StatusChip({ payment }) {
  const { tone, label } = STATUS[payment.status];
  return (
    <span className={`chip chip-${tone}`}>
      <span className="dot" aria-hidden="true" />
      {label(payment)}
    </span>
  );
}

export function Payments({ store, accounts, payments, busy, act }) {
  const [outcome, setOutcome] = useState(null);

  async function send(request) {
    const result = await act(() => store.run(request));
    const after = store.getSnapshot().accounts;
    const described = succeeded(result) ? describePayment(result.body, after) : {};
    setOutcome(outcomeOf(result, after, described.message, described.tone));
  }

  function actionsFor(payment) {
    const about = payment.description;
    if (payment.status === 'pending') {
      return (
        <>
          <button
            type="button"
            className="btn btn-outline btn-small"
            disabled={busy}
            aria-label={`Settle ${about}`}
            onClick={() => send(settle(payment.id, newKey('settle')))}
          >
            Settle
          </button>
          <button
            type="button"
            className="btn btn-quiet btn-small"
            disabled={busy}
            aria-label={`Cancel ${about}`}
            onClick={() => send(cancel(payment.id, newKey('cancel')))}
          >
            Cancel
          </button>
        </>
      );
    }
    if (payment.status === 'settled' && payment.rail === 'ach') {
      return (
        <button
          type="button"
          className="btn btn-outline btn-small"
          disabled={busy}
          aria-label={`Return ${about} as ${RETURN_REASON}`}
          onClick={() => send(giveBack(payment.id, RETURN_REASON, newKey('return')))}
        >
          Return
        </button>
      );
    }
    return <span className="final">Final</span>;
  }

  return (
    <Panel
      title="Payments"
      note="A payment only moves forward: pending, then settled or canceled. A settled ACH payment can come back."
      className="payments"
    >
      {payments.length === 0 ? (
        <p className="empty">No payments yet. Create one above, or take the tour.</p>
      ) : (
        <div className="table-wrap">
          <table className="payments-table">
            <thead>
              <tr>
                <th scope="col">Payment</th>
                <th scope="col">Rail</th>
                <th scope="col" className="num">
                  Amount
                </th>
                <th scope="col">Status</th>
                <th scope="col">Next</th>
              </tr>
            </thead>
            <tbody>
              {payments.map((payment) => (
                <tr key={payment.id}>
                  <th scope="row">
                    <span className="payment-name">{payment.description}</span>
                    <span className="payment-route">
                      {nameOf(accounts, payment.sourceAccountId)} to{' '}
                      {nameOf(accounts, payment.destinationAccountId)}
                    </span>
                  </th>
                  <td data-label="Rail">{payment.rail === 'ach' ? 'ACH' : 'Instant'}</td>
                  <td data-label="Amount" className="num amount">
                    {formatMoney(payment.amount, payment.currency)}
                  </td>
                  <td data-label="Status">
                    <StatusChip payment={payment} />
                  </td>
                  <td data-label="Next">
                    <div className="row row-tight">{actionsFor(payment)}</div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <Outcome outcome={outcome} />
    </Panel>
  );
}
