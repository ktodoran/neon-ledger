// How a response is put in front of a person: the status line a developer
// would see, and a sentence saying what it means.
//
// Tones follow the console's colors: "good" for something that happened,
// "held" for money that is waiting, "warn" for a refusal or a return.

import { formatMoney } from './lib/money.js';

const REASONS = { 200: 'OK', 201: 'Created' };

export function labelOf(result) {
  if (result.status === 0) return 'No answer';
  const base =
    result.status < 400
      ? `${String(result.status)} ${REASONS[result.status] ?? ''}`.trim()
      : `${String(result.status)} ${result.body?.code ?? 'error'}`;
  return result.replayed ? `${base} · replayed` : base;
}

export const succeeded = (result) => result.status < 400;

const REPLAYED = 'This is the first response again. The ledger did not act a second time.';

/** Swaps account ids in the ledger's own wording for the names on screen. */
function named(text, accounts) {
  return accounts.reduce((out, account) => out.replaceAll(account.id, account.name), text);
}

function refusal(result, accounts) {
  const { body } = result;
  switch (body?.code) {
    case 'ledger_unreachable':
      return `${body.detail} Check that it is still running, then try again.`;
    case 'insufficient_funds':
      return `Refused. ${named(body.detail, accounts).replace(/^Account /, '')} Nothing moved.`;
    case 'validation_failed':
      return `Refused before anything was posted. ${Object.values(body.errors ?? {})
        .flat()
        .join(' ')}`;
    case 'idempotency_key_reused':
      return 'Refused. This key was already used for a different request. A new request needs a new key.';
    case 'invalid_payment_state':
      return `Refused. ${body.detail.replace(/^Payment \S+ /, 'This payment ')}`;
    default:
      return `Refused. ${named(body?.detail ?? 'The ledger did not accept the request.', accounts)}`;
  }
}

/**
 * The outcome of one request. `success` is the sentence for when it worked;
 * a refusal or a replay is described from the response itself.
 */
export function outcomeOf(result, accounts, success, tone = 'good') {
  if (!succeeded(result)) {
    const message = refusal(result, accounts);
    return {
      label: labelOf(result),
      tone: 'warn',
      message: result.replayed ? `${message} ${REPLAYED}` : message,
    };
  }
  return {
    label: labelOf(result),
    tone,
    message: result.replayed ? REPLAYED : success,
  };
}

export const nameOf = (accounts, id) =>
  accounts.find((account) => account.id === id)?.name ?? 'an account';

export function describePayment(payment, accounts) {
  const amount = formatMoney(payment.amount, payment.currency);
  const source = nameOf(accounts, payment.sourceAccountId);
  const destination = nameOf(accounts, payment.destinationAccountId);
  switch (payment.status) {
    case 'pending':
      return {
        tone: 'held',
        message: `Payment created and pending. ${amount} is held on ${source} until it settles or is canceled.`,
      };
    case 'settled':
      return payment.rail === 'instant'
        ? {
            tone: 'good',
            message: `Settled at once. ${amount} moved from ${source} to ${destination}. An instant payment is final.`,
          }
        : {
            tone: 'good',
            message: `Settled. The hold became a transaction and ${amount} left ${source}.`,
          };
    case 'canceled':
      return {
        tone: 'good',
        message: `Canceled. ${amount} is available on ${source} again. Nothing was posted.`,
      };
    case 'returned':
      return {
        tone: 'warn',
        message: `Returned as ${payment.returnReason}. The ledger posted the opposite entries and ${amount} is back on ${source}. The original stays in the journal.`,
      };
    default:
      return { tone: 'good', message: '' };
  }
}

export function describeBooks(report) {
  if (report.consistent) {
    return {
      label: 'Consistent',
      tone: 'good',
      message: `Debits minus credits is ${formatMoney(report.debitsMinusCredits)}. Every balance was recomputed from its entries and every hold from its pending payments: no mismatches in ${String(report.accountsChecked)} accounts.`,
    };
  }
  return {
    label: 'Not consistent',
    tone: 'warn',
    message: `Debits minus credits is ${formatMoney(report.debitsMinusCredits)}. ${String(report.mismatches.length)} balance and ${String(report.holdMismatches.length)} hold mismatches in ${String(report.accountsChecked)} accounts.`,
  };
}
