import { createSimulatedLedger } from '../src/ledger/sim.js';

/** A ledger with a settlement account and two customers, and ids that read well in a failure. */
export async function openBooks(ledger = createSimulatedLedger()) {
  const open = async (name, normalBalance, allowNegative = false) =>
    (await ledger.openAccount({ name, currency: 'USD', normalBalance, allowNegative })).body.id;
  const settlement = await open('ACH settlement', 'debit', true);
  const jordan = await open('Jordan checking', 'credit');
  const riley = await open('Riley savings', 'credit');
  return { ledger, settlement, jordan, riley };
}

export const deposit = (accounts, to, amount) => ({
  description: 'Deposit',
  entries: [
    { accountId: accounts.settlement, direction: 'debit', amount },
    { accountId: to, direction: 'credit', amount },
  ],
});

export const withdrawal = (accounts, from, amount) => ({
  description: 'Withdrawal',
  entries: [
    { accountId: from, direction: 'debit', amount },
    { accountId: accounts.settlement, direction: 'credit', amount },
  ],
});

export async function balanceOf(ledger, id) {
  const { body } = await ledger.listAccounts();
  const { balance, held, available } = body.find((account) => account.id === id);
  return { balance, held, available };
}
