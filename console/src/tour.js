// The guided tour: six claims the ledger makes, each one checked by doing it.
//
// A step's actions send real requests through the same store as the rest of
// the console, and what they report is read from the response and the
// accounts afterwards. If the ledger answers differently than the step
// expects, the tour shows that answer instead.

import { giveBack, move, pay, settle } from './actions.js';
import { newKey } from './lib/keys.js';
import { formatMoney } from './lib/money.js';
import { describeBooks, outcomeOf, succeeded } from './outcome.js';

const DEPOSIT = 25000;
const BILL = 12000;
const WITHDRAWAL = 20000;
const RETURN_REASON = 'R01';

const usd = (amount) => formatMoney(amount);

function jordanAfter(store, cast) {
  const { accounts } = store.getSnapshot();
  return { accounts, jordan: accounts.find((account) => account.id === cast.jordan) };
}

export const TOUR = [
  {
    title: 'Put money in',
    body: `Deposit ${usd(DEPOSIT)} into Jordan checking. Every movement is two entries: a debit on one account and a credit on another, and the two sides have to match.`,
    look: 'Jordan checking on the left, and the new entry at the top of the journal.',
    next: 'Next: send it again',
    actions: [
      {
        label: `Deposit ${usd(DEPOSIT)}`,
        async run({ store, cast, memory }) {
          memory.deposit = {
            from: cast.settlement,
            to: cast.jordan,
            amount: DEPOSIT,
            memo: 'Paycheck',
          };
          memory.depositKey = newKey('deposit');
          const result = await store.run(move(memory.deposit, memory.depositKey));
          const { accounts } = jordanAfter(store, cast);
          return outcomeOf(result, accounts, `Posted. Two entries, ${usd(DEPOSIT)} on each side.`);
        },
      },
    ],
  },
  {
    title: 'Send it again',
    body: 'A client that times out sends the same request again with the same key. The ledger has to act once, however many times it hears it.',
    look: 'The journal. It still has one Paycheck entry.',
    next: 'Next: money on hold',
    actions: [
      {
        label: 'Send it again with the same key',
        async run({ store, cast, memory }) {
          const result = await store.run(move(memory.deposit, memory.depositKey));
          const { accounts, jordan } = jordanAfter(store, cast);
          if (!succeeded(result) || result.replayed !== true) {
            return outcomeOf(result, accounts, 'Posted a second time.', 'warn');
          }
          return {
            label: 'Replayed',
            tone: 'good',
            message: `The first response came back. Nothing was posted a second time, and Jordan still has ${usd(jordan.balance)}.`,
          };
        },
      },
    ],
  },
  {
    title: 'Money on hold',
    body: `An ACH payment does not move money when it is created. ${usd(BILL)} is set aside on Jordan checking until the payment settles or is canceled.`,
    look: 'Jordan checking: the balance is unchanged, and less of it is available.',
    next: 'Next: spend it twice',
    actions: [
      {
        label: 'Create payment',
        async run({ store, cast, memory }) {
          const result = await store.run(
            pay(
              {
                rail: 'ach',
                from: cast.jordan,
                to: cast.settlement,
                amount: BILL,
                memo: 'Electric bill',
              },
              newKey('pay'),
            ),
          );
          const { accounts, jordan } = jordanAfter(store, cast);
          if (succeeded(result)) memory.paymentId = result.body.id;
          return outcomeOf(
            result,
            accounts,
            `Pending. Balance ${usd(jordan.balance)}, held ${usd(jordan.held)}, available ${usd(jordan.available)}.`,
            'held',
          );
        },
      },
    ],
  },
  {
    title: 'Spend it twice',
    body: `Jordan's balance covers a ${usd(WITHDRAWAL)} withdrawal. The available amount does not, because ${usd(BILL)} is already promised.`,
    look: 'Nothing changes anywhere. A refusal leaves no trace in the journal.',
    next: 'Next: settle it',
    actions: [
      {
        label: `Withdraw ${usd(WITHDRAWAL)}`,
        async run({ store, cast }) {
          const result = await store.run(
            move(
              {
                from: cast.jordan,
                to: cast.settlement,
                amount: WITHDRAWAL,
                memo: 'Cash withdrawal',
              },
              newKey('withdraw'),
            ),
          );
          const { accounts } = jordanAfter(store, cast);
          if (result.body?.code !== 'insufficient_funds') {
            return outcomeOf(
              result,
              accounts,
              'Posted. The held money was spent, which it should not have been.',
              'warn',
            );
          }
          return {
            label: '422 insufficient_funds',
            tone: 'warn',
            message: 'Refused. Nothing moved, and the refusal carries a code a client can act on.',
          };
        },
      },
    ],
  },
  {
    title: 'Settle, then it comes back',
    body: 'Settling turns the hold into a real transaction. If the payment is returned, the ledger posts the opposite entries. The original is never edited or deleted.',
    look: 'The journal: the settlement, then a reversal above it.',
    next: 'Next: check the books',
    actions: [
      {
        label: 'Settle it',
        look: 'Jordan checking: the money has left and nothing is held. The settlement is at the top of the journal.',
        async run({ store, cast, memory }) {
          const result = await store.run(settle(memory.paymentId, newKey('settle')));
          const { accounts, jordan } = jordanAfter(store, cast);
          return outcomeOf(
            result,
            accounts,
            `Settled. Balance ${usd(jordan.balance)}, held ${usd(jordan.held)}. The hold became a transaction.`,
          );
        },
      },
      {
        label: 'Return it',
        async run({ store, cast, memory }) {
          const result = await store.run(
            giveBack(memory.paymentId, RETURN_REASON, newKey('return')),
          );
          const { accounts, jordan } = jordanAfter(store, cast);
          if (!succeeded(result)) return outcomeOf(result, accounts, '');
          return {
            label: `Returned · ${RETURN_REASON}`,
            tone: 'warn',
            message: `The ledger posted the opposite entries. Balance is back to ${usd(jordan.balance)}, and the journal shows both movements.`,
          };
        },
      },
    ],
  },
  {
    title: 'Check the books',
    body: 'The ledger recomputes every balance from its entries and every hold from its pending payments, and reports any that disagree with what is stored.',
    look: 'The top bar now carries the result.',
    next: 'Finish the tour',
    actions: [
      {
        label: 'Check the books',
        async run({ store }) {
          const result = await store.checkBooks();
          return succeeded(result)
            ? describeBooks(result.body)
            : outcomeOf(result, store.getSnapshot().accounts, '');
        },
      },
    ],
  },
];
