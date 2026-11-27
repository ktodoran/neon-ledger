// Three accounts to work with: where money comes from, a customer with nothing,
// and a customer with savings.
//
// A simulation starts with them so the console is not empty. On a real ledger
// the tour opens a new set and leaves every other account alone.

import { newKey } from '../lib/keys.js';

// Opened in this order because the console lists the newest account first.
const NAMES = { settlement: 'ACH settlement', riley: 'Riley savings', jordan: 'Jordan checking' };
const SAVINGS = 184000;

/** "Jordan checking", or "Jordan checking 2" when the ledger already has one. */
function freshName(name, taken) {
  if (!taken.has(name)) return name;
  let number = 2;
  while (taken.has(`${name} ${String(number)}`)) number += 1;
  return `${name} ${String(number)}`;
}

/** Opens the accounts and returns their ids by role, or null if the ledger refused. */
export async function seed(ledger, existing = []) {
  const taken = new Set(existing.map((account) => account.name));
  const cast = {};
  for (const [role, name] of Object.entries(NAMES)) {
    const settlement = role === 'settlement';
    const result = await ledger.openAccount({
      name: freshName(name, taken),
      currency: 'USD',
      // The settlement account is debit-normal and may go below zero, because
      // the bank on the other side of it is not modeled here.
      normalBalance: settlement ? 'debit' : 'credit',
      allowNegative: settlement,
    });
    if (result.status !== 201) return null;
    cast[role] = result.body.id;
  }

  const funded = await ledger.postTransaction(
    {
      description: 'Opening deposit',
      entries: [
        { accountId: cast.settlement, direction: 'debit', amount: SAVINGS },
        { accountId: cast.riley, direction: 'credit', amount: SAVINGS },
      ],
    },
    newKey('seed'),
  );
  return funded.status === 201 ? cast : null;
}
