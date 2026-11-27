import { describe, expect, it } from 'vitest';
import { readFeed, titleOf } from '../src/ledger/view.js';
import { formatMoney, parseMoney } from '../src/lib/money.js';
import { deposit, openBooks } from './helpers.js';

describe('money', () => {
  it('formats minor units as dollars', () => {
    expect(formatMoney(25000)).toBe('$250.00');
    expect(formatMoney(209000)).toBe('$2,090.00');
    expect(formatMoney(-150)).toBe('-$1.50');
  });

  it.each([
    ['120', 12000],
    ['$120.00', 12000],
    ['1,200.5', 120050],
    [' 0.07 ', 7],
  ])('reads %j as %d minor units', (text, minorUnits) => {
    expect(parseMoney(text)).toBe(minorUnits);
  });

  it.each(['', '0', '-5', '1.234', 'ten', '1e3', '.5.', '$'])('refuses %j', (text) => {
    expect(parseMoney(text)).toBeNull();
  });
});

describe('reading the feed', () => {
  it('builds the journal and the latest state of each payment, newest first', async () => {
    const books = await openBooks();
    await books.ledger.postTransaction(deposit(books, books.jordan, 25000), 'a');
    const { body: payment } = await books.ledger.createPayment(
      {
        rail: 'ach',
        sourceAccountId: books.jordan,
        destinationAccountId: books.settlement,
        amount: 12000,
        description: 'Electric bill',
      },
      'b',
    );
    await books.ledger.settlePayment(payment.id, 'c');
    await books.ledger.returnPayment(payment.id, 'R01', 'd');

    const { transactions, payments } = readFeed((await books.ledger.events()).body.items);

    expect(payments).toHaveLength(1);
    expect(payments[0].status).toBe('returned');
    expect(transactions.map((transaction) => titleOf(transaction, transactions))).toEqual([
      'Reversal: Electric bill',
      'Electric bill',
      'Deposit',
    ]);
  });
});
